using Cortex.Application.Agents;
using Cortex.Application.Approvals;
using Cortex.Application.Authorization;
using Cortex.Application.Connectors;
using Cortex.Core.Identity;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Approvals;
using Cortex.Modules.Sdk;

namespace Cortex.AspNetCore.Endpoints;

/// <summary>
/// The human-in-the-loop approval surface. When the agent tries to call a side-effecting tool it is
/// blocked and recorded as a pending approval (see <c>ToolInvocationMiddleware</c>). These endpoints let
/// an authorized human review the pending action, then either approve it — which re-executes that exact
/// tool call with its recorded arguments — or reject it.
/// </summary>
public static class ApprovalEndpoints
{
    public static void MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat/approvals").WithTags("Approvals").RequireAuthorization();

        group.MapGet("/", async (
                IApprovalStore store,
                IToolRegistry toolRegistry,
                IConnectorToolCatalog connectorTools,
                IServiceProvider services,
                CancellationToken ct) =>
            {
                var pending = await store.ListPendingAsync(ct);
                // Each item is enriched from its DECLARING tool (risk tier + human description) at
                // read time — the declaration is the living source of truth, so a re-tiered tool
                // renders at its current risk without a data migration. An unresolvable tool
                // (module uninstalled since the block) fails safe to the full high-risk card.
                var connector = pending.Count > 0
                    ? await connectorTools.GetEnabledToolsAsync(services, ct)
                    : [];
                var byModule = new Dictionary<string, IReadOnlyList<ModuleTool>>(StringComparer.Ordinal);
                var dtos = pending.Select(p =>
                {
                    if (!byModule.TryGetValue(p.ModuleId, out var tools))
                    {
                        tools = toolRegistry.GetModuleTools(p.ModuleId, services);
                        byModule[p.ModuleId] = tools;
                    }
                    var tool = tools.FirstOrDefault(t => string.Equals(t.Name, p.ToolName, StringComparison.Ordinal))
                        ?? connector.FirstOrDefault(t => string.Equals(t.Name, p.ToolName, StringComparison.Ordinal));
                    return ToDto(p, tool);
                }).ToArray();
                return Results.Ok(dtos);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageApprovals))
            .WithName("Approvals_ListPending");

        group.MapPost("/{id:guid}/approve", async (
                Guid id, IApprovalStore store, ApprovalExecutor executor, ICurrentUser current,
                IServiceProvider services, CancellationToken ct) =>
            {
                var pending = await store.TryBeginExecutionAsync(
                    id, current.UserId, current.DisplayName, ct);
                if (pending is null)
                {
                    return Results.NotFound();
                }

                var outcome = await executor.ExecuteAsync(pending, services, ct);
                // The resolver's identity is part of the oversight record — "approved by whom" is
                // exactly what the ADMT disclosure view (DisclosureEndpoints) has to answer.
                await store.CompleteExecutionAsync(
                    id,
                    outcome.Success ? ApprovalStatus.Executed : ApprovalStatus.Failed,
                    outcome.Result,
                    outcome.Error,
                    ct);

                return outcome.Success
                    ? Results.Ok(new { id, status = nameof(ApprovalStatus.Executed), result = outcome.Result })
                    : Results.Problem(detail: outcome.Error, statusCode: 422);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageApprovals))
            .WithName("Approvals_Approve");

        group.MapPost("/{id:guid}/reject", async (Guid id, IApprovalStore store, ICurrentUser current, CancellationToken ct) =>
            {
                var rejected = await store.TryRejectAsync(id, current.UserId, current.DisplayName, ct);
                if (!rejected)
                {
                    return Results.NotFound();
                }

                return Results.Ok(new { id, status = nameof(ApprovalStatus.Rejected) });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageApprovals))
            .WithName("Approvals_Reject");
    }

    private static ApprovalDto ToDto(PendingApproval p, ModuleTool? tool) =>
        new(
            p.Id, p.ConversationId, p.ModuleId, p.ToolName, p.ArgumentsJson, p.UserDisplay, p.CreatedAt,
            // Lowercase string literal on the wire (the shell switches on it), same contract style
            // as the chart kind. Unresolvable → high: never render less ceremony than declared.
            tool?.Risk == ApprovalRisk.Low ? "low" : "high",
            tool?.Function.Description is { Length: > 0 } d ? d : null);

    private sealed record ApprovalDto(
        Guid Id, Guid ConversationId, string ModuleId, string ToolName, string? ArgumentsJson, string? UserDisplay, DateTimeOffset CreatedAt,
        string Risk, string? Description);
}

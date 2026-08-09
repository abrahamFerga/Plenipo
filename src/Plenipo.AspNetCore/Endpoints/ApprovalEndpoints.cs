using Plenipo.Application.Agents;
using Plenipo.Application.Approvals;
using Plenipo.Application.Authorization;
using Plenipo.Application.Connectors;
using Plenipo.Core.Identity;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Approvals;
using Plenipo.Modules.Sdk;

namespace Plenipo.AspNetCore.Endpoints;

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
                Guid id, IApprovalStore store, ApprovalExecutor executor, ApprovalResolutionAnnouncer announcer,
                ICurrentUser current, IServiceProvider services, CancellationToken ct) =>
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
                var status = outcome.Success ? ApprovalStatus.Executed : ApprovalStatus.Failed;
                await store.CompleteExecutionAsync(id, status, outcome.Result, outcome.Error, ct);

                // The requester must not have to ask whether their click did anything: write the
                // outcome into the conversation and ping them (best-effort; never fails the approve).
                // The composed note comes back so the shell can echo the SAME wording at the click site.
                var note = await announcer.AnnounceAsync(
                    pending, status, outcome.Result, outcome.Error, current.UserId, current.DisplayName, ct);

                return outcome.Success
                    ? Results.Ok(new { id, status = nameof(ApprovalStatus.Executed), result = outcome.Result, note })
                    : Results.Problem(detail: outcome.Error, statusCode: 422);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageApprovals))
            .WithName("Approvals_Approve");

        group.MapPost("/{id:guid}/reject", async (
                Guid id, IApprovalStore store, ApprovalResolutionAnnouncer announcer,
                ICurrentUser current, CancellationToken ct) =>
            {
                // Fetched before the atomic reject so the announcement has the tool/conversation
                // context; the reject itself still decides who won a race.
                var pending = await store.GetAsync(id, ct);
                var rejected = await store.TryRejectAsync(id, current.UserId, current.DisplayName, ct);
                if (!rejected)
                {
                    return Results.NotFound();
                }

                string? note = null;
                if (pending is not null)
                {
                    note = await announcer.AnnounceAsync(
                        pending, ApprovalStatus.Rejected, result: null, error: null,
                        current.UserId, current.DisplayName, ct);
                }

                return Results.Ok(new { id, status = nameof(ApprovalStatus.Rejected), note });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageApprovals))
            .WithName("Approvals_Reject");
    }

    private static ApprovalDto ToDto(PendingApproval p, ModuleTool? tool) =>
        new(
            p.Id, p.ConversationId, p.ModuleId, p.ToolName, p.ArgumentsJson,
            // UserDisplay is a best-effort label captured at block time and is NOT an identifier —
            // two people can share one, and under dev-auth every subject shares "Dev User". UserId
            // is the stable one, so an approver can tell whose work they are signing off. Projected
            // rather than looked up: the model already holds it, so this costs no query and no
            // migration. Null only for the pre-identity rows that predate it.
            p.UserId, p.UserDisplay, p.CreatedAt,
            // Lowercase string literal on the wire (the shell switches on it), same contract style
            // as the chart kind. Unresolvable → high: never render less ceremony than declared.
            tool?.Risk == ApprovalRisk.Low ? "low" : "high",
            tool?.Function.Description is { Length: > 0 } d ? d : null);

    private sealed record ApprovalDto(
        Guid Id, Guid ConversationId, string ModuleId, string ToolName, string? ArgumentsJson,
        Guid? UserId, string? UserDisplay, DateTimeOffset CreatedAt,
        string Risk, string? Description);
}

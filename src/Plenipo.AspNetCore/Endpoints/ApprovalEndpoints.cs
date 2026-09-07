using Plenipo.Application.Agents;
using Plenipo.Application.Approvals;
using Plenipo.Application.Auditing;
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
/// tool call with its recorded arguments, as the requester — or reject it.
/// </summary>
public static class ApprovalEndpoints
{
    public static void MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat/approvals").WithTags("Approvals").RequireAuthorization();

        // conversationId narrows the queue to one conversation (#111): "approve this" is informed
        // consent only when the caller can see what asked for it. Absent, this is the tenant-wide
        // review queue the admin surface renders.
        group.MapGet("/", async (
                Guid? conversationId,
                IApprovalStore store,
                IToolRegistry toolRegistry,
                IConnectorToolCatalog connectorTools,
                IServiceProvider services,
                CancellationToken ct) =>
            {
                var pending = await store.ListPendingAsync(conversationId, ct);
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
                Guid id, HttpContext http, IApprovalStore store, ApprovalExecutor executor, ApprovalRelease release,
                ApprovalResolutionAnnouncer announcer, IAuditLog auditLog, ICurrentUser current,
                IServiceProvider services, CancellationToken ct) =>
            {
                var parked = await store.GetAsync(id, ct);
                if (parked is null || parked.Status != ApprovalStatus.Pending)
                {
                    return Results.NotFound();
                }

                // The approvals permission opens the queue; it does not grant the tool (#145). The
                // permission that gated the tool when it was proposed gates it again at release, so
                // the queue is never a way to perform an action the role model withholds. Refused
                // before the claim, so the action stays pending for someone who may release it.
                var tool = await executor.ResolveToolAsync(parked, services, ct);
                if (tool is not null && !current.HasPermission(tool.Permission))
                {
                    var detail = $"POST /api/chat/approvals/{id}/approve requires {tool.Permission} to release " +
                                 $"'{tool.Name}' ({parked.ModuleId}); the caller holds {Permissions.ManageApprovals} only";
                    await auditLog.RecordAuthEventAsync(new AuthAuditEntry
                    {
                        TenantId = current.TenantId,
                        UserId = current.UserId,
                        Subject = current.Subject,
                        UserDisplay = current.DisplayName,
                        EventType = AuthAuditEventType.AccessDenied,
                        Detail = detail,
                        IpAddress = IpOf(http),
                    }, ct);
                    return Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Releasing this action needs the tool's own permission.",
                        detail: detail);
                }

                var pending = await store.TryBeginExecutionAsync(
                    id, current.UserId, current.DisplayName, ct);
                if (pending is null)
                {
                    return Results.NotFound();
                }

                // Runs as the REQUESTER (identity + snapshotted authority), never as the approver, and
                // audits both the execution and the decision (#153, #88).
                var outcome = await release.ExecuteAsRequesterAsync(pending, current, IpOf(http), ct);
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
                Guid id, HttpContext http, IApprovalStore store, ApprovalResolutionAnnouncer announcer,
                IAuditLog auditLog, ICurrentUser current, CancellationToken ct) =>
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
                    // A "no" is a decision too, and belongs on the append-only trail next to the "yes".
                    await auditLog.RecordAuthEventAsync(new AuthAuditEntry
                    {
                        TenantId = current.TenantId,
                        UserId = current.UserId,
                        Subject = current.Subject,
                        UserDisplay = current.DisplayName,
                        EventType = AuthAuditEventType.ApprovalDecided,
                        Detail = $"rejected {pending.ToolName} ({pending.ModuleId}) requested by " +
                                 $"{pending.UserDisplay ?? pending.RequesterSubject ?? "an unknown user"}; approval {pending.Id}",
                        IpAddress = IpOf(http),
                    }, ct);

                    note = await announcer.AnnounceAsync(
                        pending, ApprovalStatus.Rejected, result: null, error: null,
                        current.UserId, current.DisplayName, ct);
                }

                return Results.Ok(new { id, status = nameof(ApprovalStatus.Rejected), note });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageApprovals))
            .WithName("Approvals_Reject");
    }

    private static string? IpOf(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

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

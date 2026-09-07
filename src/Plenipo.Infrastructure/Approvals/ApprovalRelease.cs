using System.Diagnostics;
using System.Text.Json;
using Plenipo.Application.Auditing;
using Plenipo.Core.Identity;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Context;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Plenipo.Infrastructure.Approvals;

/// <summary>
/// Releases an approved, parked tool call the way the platform's other deferred executions do
/// (<c>JobProcessor</c>, the channel turn service): in a fresh scope carrying the <em>requester's</em>
/// identity and the authority snapshotted when the call was parked — never the approver's request
/// scope. A tool that resolves <see cref="ICurrentUser"/> therefore attributes the write to the human
/// who asked for it, which is what separation-of-duties controls and trust-accounting attribution
/// depend on (#153), while the approver is recorded separately as the resolver.
/// <para>
/// The release is audited twice, on purpose: a tool-call row marks the execution as executed (or
/// failed) under the requester (#88 — the park already wrote its "blocked" row, and without this the
/// trail affirmatively misstated real writes), and an <see cref="AuthAuditEventType.ApprovalDecided"/>
/// event names the approver, the tool, the requester and the outcome.
/// </para>
/// </summary>
public sealed class ApprovalRelease(IServiceScopeFactory scopeFactory, ILogger<ApprovalRelease> logger)
{
    /// <summary>
    /// Executes <paramref name="approval"/> as its requester and audits the execution and the decision.
    /// <paramref name="approver"/> is the human who released it; <paramref name="ipAddress"/> is their
    /// request's origin, for the audit rows.
    /// </summary>
    public async Task<ApprovalExecutionResult> ExecuteAsRequesterAsync(
        PendingApproval approval, ICurrentUser approver, string? ipAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(approver);

        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.SetTenant(approval.TenantId);

        var (subject, display, blocked) = await RestoreRequesterAsync(services, context, approval, cancellationToken);
        if (blocked is not null)
        {
            return await RecordAsync(services, approval, approver, tool: null, subject, display,
                new ApprovalExecutionResult(false, null, blocked), durationMs: 0, ipAddress, cancellationToken);
        }

        // The authority the runner already checked before the model saw the tool. Rows parked before
        // the snapshot column existed fall back to the approver's authority, which is what every
        // release used before this existed — narrower would silently fail old approvals.
        var permissions = approval.PermissionsSnapshotJson is { Length: > 0 } json
            ? JsonSerializer.Deserialize<string[]>(json) ?? []
            : approver.Permissions.ToArray();
        context.SetPermissions(permissions);

        var executor = services.GetRequiredService<ApprovalExecutor>();
        var tool = await executor.ResolveToolAsync(approval, services, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var outcome = tool is null
            ? new ApprovalExecutionResult(false, null, $"Tool '{approval.ToolName}' is no longer available.")
            : await ApprovalExecutor.InvokeAsync(tool, approval, cancellationToken);
        stopwatch.Stop();

        return await RecordAsync(services, approval, approver, tool, subject, display, outcome,
            stopwatch.ElapsedMilliseconds, ipAddress, cancellationToken);
    }

    private static async Task<(string? Subject, string? Display, string? Blocked)> RestoreRequesterAsync(
        IServiceProvider services, RequestContext context, PendingApproval approval, CancellationToken cancellationToken)
    {
        var subject = approval.RequesterSubject;
        var display = approval.UserDisplay;

        if (approval.UserId is { } userId)
        {
            var db = services.GetRequiredService<PlatformDbContext>();
            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is not null)
            {
                subject ??= user.Subject;
                display ??= user.DisplayName;
                if (!user.IsActive)
                {
                    return (subject, display, "The requester's account has been deactivated, so the action was not executed.");
                }
            }

            context.SetUser(userId, subject ?? string.Empty, display);
        }
        else if (subject is not null)
        {
            context.SetIdentity(subject, display);
        }

        return (subject, display, null);
    }

    private async Task<ApprovalExecutionResult> RecordAsync(
        IServiceProvider services, PendingApproval approval, ICurrentUser approver, Plenipo.Modules.Sdk.ModuleTool? tool,
        string? requesterSubject, string? requesterDisplay, ApprovalExecutionResult outcome, long durationMs,
        string? ipAddress, CancellationToken cancellationToken)
    {
        var audit = services.GetRequiredService<IAuditLog>();
        try
        {
            // The park already recorded the (redacted) arguments; the execution row records the outcome.
            await audit.RecordToolCallAsync(new ToolCallAuditEntry
            {
                TenantId = approval.TenantId,
                UserId = approval.UserId,
                UserDisplay = requesterDisplay,
                ModuleId = approval.ModuleId,
                ToolName = approval.ToolName,
                Permission = tool?.Permission ?? string.Empty,
                ConversationId = approval.ConversationId,
                Success = outcome.Success,
                Error = outcome.Error,
                DurationMs = durationMs,
                IpAddress = ipAddress,
            }, cancellationToken);

            await audit.RecordAuthEventAsync(new AuthAuditEntry
            {
                TenantId = approval.TenantId,
                UserId = approver.UserId,
                Subject = approver.Subject,
                UserDisplay = approver.DisplayName,
                EventType = AuthAuditEventType.ApprovalDecided,
                Detail = $"approved {approval.ToolName} ({approval.ModuleId}) requested by " +
                         $"{requesterDisplay ?? requesterSubject ?? "an unknown user"}; approval {approval.Id}; " +
                         (outcome.Success ? "executed" : $"failed: {outcome.Error}"),
                IpAddress = ipAddress,
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The audit store is append-only and best-effort by contract (writes never fail the
            // operation); the execution outcome itself is already on the pending-approval row.
            logger.LogError(ex, "Could not audit the release of approval {ApprovalId} ({ToolName}).", approval.Id, approval.ToolName);
        }

        return outcome;
    }
}

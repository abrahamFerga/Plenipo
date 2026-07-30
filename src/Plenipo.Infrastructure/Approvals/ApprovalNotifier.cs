using Plenipo.Application.Authorization;
using Plenipo.Application.Notifications;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Approvals;

/// <summary>
/// Tells the people who can ACT on a pending approval that one is waiting — approvers should not
/// have to camp in chat to notice blocked actions. Called right after the pending row is recorded;
/// emits one in-app notification per approver through <see cref="INotifier"/>, which also applies
/// each recipient's own category mute (category <c>"{moduleId}.approvals"</c> — a module declares
/// it in <c>NotificationCategories</c> to surface the mute switch; undeclared it still delivers,
/// default-on).
///
/// <para><b>Who counts as an approver.</b> Every active tenant user whose DATABASE-sourced
/// authority grants <see cref="Permissions.ManageApprovals"/>: DB role assignments expanded from the
/// declared baseline adjusted by the tenant's stored deviations — the same rules the request path uses
/// via <c>RolePermissionResolution</c> — unioned with explicit per-user grants. This is the pragmatic query: roles asserted only in a
/// user's TOKEN deliberately have no DB rows (see <c>RequestEnricher</c>), so a token-only
/// approver cannot be enumerated here and simply gets no ping — under-notifying, never
/// over-authorizing (the approval endpoints still gate on the real permission at click time).
/// Deployments on <c>Auth:PermissionSource=Token</c> should mirror approver roles into DB
/// assignments if they want these notifications.</para>
///
/// <para>Best-effort by contract: this never throws — a notification hiccup must not fail the
/// agent turn that recorded the approval.</para>
/// </summary>
public sealed class ApprovalNotifier(
    PlatformDbContext db,
    INotifier notifier,
    IEnumerable<ProductRole> productRoles,
    IOptions<AuthorizationSourceOptions> authorizationSource,
    ILogger<ApprovalNotifier> logger)
{
    public async Task NotifyPendingAsync(PendingApproval pending, CancellationToken cancellationToken = default)
    {
        try
        {
            var approverIds = await ResolveApproverIdsAsync(cancellationToken);
            if (approverIds.Count == 0)
            {
                // A tenant with a pending approval and nobody who can clear it is a LOCKOUT: every
                // approval-gated write parks until an operator intervenes. Outside token-sourced
                // deployments — where having no DB-enumerable approver is expected and documented in
                // the class docs above — say so loudly, because the state is otherwise invisible and
                // the only symptom is work silently not happening.
                if (authorizationSource.Value.IsTokenSourced)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "No DB-enumerable approvers for pending approval {ApprovalId}; no notifications sent.",
                            pending.Id);
                    }
                }
                else
                {
                    logger.LogWarning(
                        "No user in tenant {TenantId} holds {Permission}, so pending approval {ApprovalId} has no " +
                        "eligible approver and the action stays blocked. Grant the permission to a role in " +
                        "Admin → Roles, or assign a role that already holds it.",
                        pending.TenantId, Permissions.ManageApprovals, pending.Id);
                }

                return;
            }

            var notification = new Notification(
                pending.TenantId,
                Guid.Empty, // per-recipient below
                Category: $"{pending.ModuleId}.approvals",
                Title: $"Approval needed: {pending.ToolName}",
                Body: $"{pending.UserDisplay ?? "Someone"} asked the agent to run '{pending.ToolName}' " +
                      $"in the {pending.ModuleId} module. The action is blocked until an approver decides.",
                Link: "/chat"); // the approvals queue renders on the chat surface

            foreach (var userId in approverIds)
            {
                // One per approver; the notifier itself drops it for anyone who muted the category.
                await notifier.NotifyAsync(notification with { UserId = userId }, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not notify approvers about pending approval {ApprovalId} (the approval itself is recorded).",
                pending.Id);
        }
    }

    /// <summary>
    /// Active tenant users whose DB roles/grants satisfy <see cref="Permissions.ManageApprovals"/>.
    /// Runs inside the turn's tenant scope, so every query below is already tenant-filtered.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveApproverIdsAsync(CancellationToken cancellationToken)
    {
        var userIds = await db.Users
            .Where(u => u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        var rolesByUser = (await db.UserRoles
                .Select(r => new { r.UserId, r.Role })
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Role).ToList());

        var grantsByUser = (await db.UserPermissions
                .Select(p => new { p.UserId, p.Permission })
                .ToListAsync(cancellationToken))
            .GroupBy(p => p.UserId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Permission).ToList());

        // The tenant's deviations from the declared baseline — identical expansion rules to
        // PermissionResolver on the request path, so an approver the platform enumerates here is
        // exactly an approver the request pipeline would authorize.
        var grantedByRole = await GroupByRoleAsync(
            db.RolePermissions.Select(r => new RoleRow(r.Role, r.Permission)), cancellationToken);
        var suppressedByRole = await GroupByRoleAsync(
            db.RolePermissionSuppressions.Select(r => new RoleRow(r.Role, r.Permission)), cancellationToken);
        var baseline = RoleBaseline.Merge(productRoles);

        var approvers = new List<Guid>();
        foreach (var userId in userIds)
        {
            var permissions = new HashSet<string>(StringComparer.Ordinal);
            if (rolesByUser.TryGetValue(userId, out var roles))
            {
                permissions.UnionWith(RolePermissionResolution.PermissionsForRoles(
                    roles, grantedByRole, suppressedByRole, baseline));
            }

            if (grantsByUser.TryGetValue(userId, out var grants))
            {
                permissions.UnionWith(grants);
            }

            if (PermissionMatcher.IsGranted(permissions, Permissions.ManageApprovals))
            {
                approvers.Add(userId);
            }
        }

        return approvers;
    }

    private sealed record RoleRow(string Role, string Permission);

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GroupByRoleAsync(
        IQueryable<RoleRow> rows, CancellationToken cancellationToken) =>
        (await rows.ToListAsync(cancellationToken))
            .GroupBy(r => r.Role, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Permission).ToList(),
                StringComparer.Ordinal);
}

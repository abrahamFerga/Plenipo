using Cortex.Application.Authorization;
using Cortex.Application.Notifications;
using Cortex.Application.Usage;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cortex.Infrastructure.Usage;

/// <summary>
/// Turns monthly-budget threshold crossings into notifications for the tenant's admins. Fired
/// once per crossing (the turn that moved consumption over the line); when a single turn crosses
/// both 80% and 100%, only the more urgent exhaustion alert is sent. Recipients are the tenant's
/// role-row admins plus the acting user when they hold the AI-settings permission — the latter
/// covers Token authorization mode, where admin rights live in claims and no role rows exist.
/// Best-effort: an alert failure never affects the chat turn that triggered it.
/// </summary>
public sealed class BudgetAlerts(
    PlatformDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser,
    INotifier notifier,
    ILogger<BudgetAlerts> logger)
{
    public const double WarnFraction = 0.8;

    public async Task NotifyCrossingsAsync(long before, long after, long budget, CancellationToken cancellationToken = default)
    {
        try
        {
            string? title = null, body = null;
            if (TokenBudget.CrossedFraction(before, after, budget, 1.0))
            {
                title = "Monthly token budget exhausted";
                body = $"The tenant has used {after:N0} of its {budget:N0}-token monthly budget. " +
                       "Chat is refused until the budget is raised or the month rolls over.";
            }
            else if (TokenBudget.CrossedFraction(before, after, budget, WarnFraction))
            {
                title = "Monthly token budget at 80%";
                body = $"The tenant has used {after:N0} of its {budget:N0}-token monthly budget. " +
                       "Chat keeps working; consider raising the budget if this pace is expected.";
            }

            if (title is null || tenant.TenantId is not Guid tenantId)
            {
                return;
            }

            var adminIds = (await db.UserRoles
                .Where(r => r.Role == Roles.TenantAdmin)
                .Select(r => r.UserId)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet();
            if (currentUser.UserId is Guid actingId && currentUser.HasPermission(Permissions.ManageAiSettings))
            {
                adminIds.Add(actingId);
            }

            foreach (var adminId in adminIds)
            {
                await notifier.NotifyAsync(new Notification(
                    tenantId, adminId, "budget", title, body!, "/api/admin/usage?days=30"), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Budget alert failed (alerts are best-effort)");
        }
    }
}

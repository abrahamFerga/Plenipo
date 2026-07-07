using Cortex.Application.Commerce;
using Cortex.Application.Modules;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cortex.Infrastructure.Commerce;

/// <summary>
/// The provisioning transaction (see <see cref="ITenantProvisioningService"/>). Explicitly stamps
/// the new tenant's id on every row so it works from ANY context — an operator's admin request or
/// the billing worker's background scope (no ambient tenant). One SaveChanges: all or nothing.
/// </summary>
public sealed class TenantProvisioningService(
    PlatformDbContext db,
    IModuleCatalog catalog,
    IEnumerable<ITenantProvisionedHook> hooks,
    ILogger<TenantProvisioningService> logger) : ITenantProvisioningService
{
    public async Task<ProvisionResult> ProvisionAsync(
        ProvisionTenantCommand command, CancellationToken cancellationToken = default)
    {
        var name = command.Name?.Trim();
        var slug = command.Slug?.Trim().ToLowerInvariant();
        var adminEmail = command.AdminEmail?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(adminEmail))
        {
            return new(ProvisionError.Invalid, "name, slug, and adminEmail are required.");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(slug, "^[a-z0-9][a-z0-9-]{0,62}$"))
        {
            return new(ProvisionError.Invalid, "slug must be lowercase letters, digits, and hyphens (max 63 chars).");
        }

        if (command.MaxSeats is < 1)
        {
            return new(ProvisionError.Invalid, "maxSeats must be at least 1.");
        }

        if (command.MonthlyTokenBudget is < 0)
        {
            return new(ProvisionError.Invalid, "monthlyTokenBudget must be zero or greater.");
        }

        var unknownModules = (command.Modules ?? [])
            .Where(m => !catalog.Manifests.Any(mf => string.Equals(mf.Id, m, StringComparison.Ordinal)))
            .ToList();
        if (unknownModules.Count > 0)
        {
            return new(ProvisionError.Invalid, $"Unknown module(s): {string.Join(", ", unknownModules)}.");
        }

        if (await db.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken))
        {
            return new(ProvisionError.SlugTaken, $"A tenant with slug '{slug}' already exists.");
        }

        var tenant = new Tenant { Name = name, Slug = slug, MaxSeats = command.MaxSeats };
        db.Tenants.Add(tenant);

        // The first admin. The subject must match what the IdP will present as `sub` for this
        // person (dev auth: the X-Dev-Subject header). It defaults to the email — right for
        // email-subject IdP setups and dev; in Token permission-source mode the row is mostly
        // informational since roles come from the token.
        var admin = new User
        {
            TenantId = tenant.Id,
            Subject = string.IsNullOrWhiteSpace(command.AdminSubject) ? adminEmail : command.AdminSubject.Trim(),
            Email = adminEmail,
            DisplayName = command.AdminDisplayName?.Trim(),
        };
        db.Users.Add(admin);
        db.UserRoles.Add(new UserRole { TenantId = tenant.Id, UserId = admin.Id, Role = "tenant_admin" });

        // Modules are default-ON; licensing a subset means explicitly disabling the rest.
        if (command.Modules is not null)
        {
            var licensed = command.Modules.ToHashSet(StringComparer.Ordinal);
            foreach (var manifest in catalog.Manifests.Where(m => !licensed.Contains(m.Id)))
            {
                db.TenantModules.Add(new TenantModule { TenantId = tenant.Id, ModuleId = manifest.Id, IsEnabled = false });
            }
        }

        // Platform-managed (metered) AI: cap spend with the monthly budget; the deployment's
        // provider/key applies. BYO-key customers set their own connection later in AI Settings
        // (a tenant-owned connection never falls back to the platform key).
        if (command.MonthlyTokenBudget is { } budget)
        {
            db.TenantAiSettings.Add(new TenantAiSettings { TenantId = tenant.Id, MaxMonthlyTokens = budget });
        }

        await db.SaveChangesAsync(cancellationToken); // one transaction — all of it lands or none of it

        // Product hooks run AFTER the commit: the tenant exists whatever a hook does. Failures are
        // logged, never propagated — a broken welcome email must not roll back a paid workspace.
        var enabledModules = (command.Modules ?? catalog.Manifests.Select(m => m.Id).ToList()).ToArray();
        var context = new TenantProvisionedContext(
            tenant.Id, tenant.Slug, tenant.Name, admin.Id, admin.Email, admin.Subject,
            enabledModules, tenant.MaxSeats, command.MonthlyTokenBudget);
        foreach (var hook in hooks)
        {
            try
            {
                await hook.OnTenantProvisionedAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Tenant-provisioned hook {Hook} failed for tenant {Slug}.",
                    hook.GetType().Name, tenant.Slug);
            }
        }

        return new(ProvisionError.None, null, tenant.Id, admin.Id, admin.Subject, enabledModules);
    }
}

using Plenipo.Application.Authorization;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.AspNetCore.Setup;

/// <summary>
/// One-shot upgrade from legacy full-set role storage to deviation storage.
///
/// <para>Before this release a tenant's first seed materialized EVERY role's entire permission set as
/// rows, and any row at all made the tenant authoritative — so a role with no rows granted nothing and a
/// product's later <c>AddPlenipoRole</c> change could never reach that tenant. Now a tenant stores only
/// what it CHANGED: <c>effective = (baseline ∖ suppressed) ∪ granted</c>.</para>
///
/// <para><b>Lossless by construction.</b> For a role present in both the legacy rows <c>C</c> and the
/// declared baseline <c>B</c>, the conversion writes suppressions <c>B ∖ C</c> and keeps grants
/// <c>C ∖ B</c>, giving <c>(B ∖ (B ∖ C)) ∪ (C ∖ B) = (B ∩ C) ∪ (C ∖ B) = C</c>. Every principal's
/// effective permission set is identical before and after. The upgrade changes nobody's permissions —
/// which is the only contract that is safe to apply unattended across every existing deployment.</para>
///
/// <para>Idempotent: a converted tenant is stamped with <see cref="Tenant.RolePermissionsConvertedAt"/>
/// and skipped, so restarts and concurrent instances converge. Self-disabling — once every tenant is
/// stamped this costs one indexed query per startup and is never dropped into again.</para>
/// </summary>
public static class RoleStorageConversion
{
    /// <summary>Converts every not-yet-converted tenant. Returns how many were converted.</summary>
    public static async Task<int> ConvertAsync(
        PlatformDbContext platform,
        IReadOnlyDictionary<string, string[]> baseline,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(logger);

        // Tenants are not tenant-owned, so no query filter applies here.
        var pending = await platform.Tenants
            .Where(t => t.RolePermissionsConvertedAt == null)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return 0;
        }

        var converted = 0;
        foreach (var tenant in pending)
        {
            converted += await ConvertTenantAsync(platform, tenant, baseline, logger, cancellationToken) ? 1 : 0;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Role permission storage converted to the deviation model for {Count} tenant(s).", converted);
        }

        return converted;
    }

    private static async Task<bool> ConvertTenantAsync(
        PlatformDbContext platform,
        Tenant tenant,
        IReadOnlyDictionary<string, string[]> baseline,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // RolePermission is tenant-owned, but no ambient tenant is set during startup initialization, so
        // bypass the query filter and scope explicitly by tenant id (mirrors the seeding this replaces).
        var legacyRows = await platform.RolePermissions.IgnoreQueryFilters()
            .Where(rp => rp.TenantId == tenant.Id)
            .ToListAsync(cancellationToken);

        // A tenant that was never seeded already tracks the baseline: it converts to zero deviations,
        // which is exactly what it resolved to before. Stamp it and move on.
        if (legacyRows.Count == 0)
        {
            tenant.RolePermissionsConvertedAt = DateTimeOffset.UtcNow;
            await platform.SaveChangesAsync(cancellationToken);
            return true;
        }

        var configured = legacyRows
            .GroupBy(r => r.Role, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.Permission).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        var suppressions = new List<RolePermissionSuppression>();
        var retainedGrants = 0;

        foreach (var role in configured.Keys.Union(baseline.Keys, StringComparer.Ordinal))
        {
            // system_admin is fixed at the global wildcard by RolePermissionResolution, which
            // short-circuits before consulting any row — so its seeded rows have always been inert.
            // Drop them rather than carrying a misleading record forward.
            if (string.Equals(role, Roles.SystemAdmin, StringComparison.Ordinal))
            {
                continue;
            }

            var hasRows = configured.TryGetValue(role, out var current);
            var hasBaseline = baseline.TryGetValue(role, out var declared);

            if (!hasBaseline)
            {
                // A custom role the tenant defined at runtime: it has no baseline, so every row is a
                // grant and stays exactly as it is.
                retainedGrants += current!.Count;
                continue;
            }

            // Everything the baseline declares but this tenant does not currently grant is withheld —
            // whether because an admin removed it or because the product declared it after this tenant
            // was seeded. Both cases resolve to "not granted" today, and preserving that is the point:
            // the conversion must not hand anyone a permission they did not already have.
            var withheld = hasRows ? declared!.Except(current!, StringComparer.Ordinal) : declared!;
            foreach (var permission in withheld)
            {
                suppressions.Add(new RolePermissionSuppression
                {
                    TenantId = tenant.Id,
                    Role = role,
                    Permission = permission,
                });
            }

            if (hasRows)
            {
                retainedGrants += current!.Count(p => !declared!.Contains(p, StringComparer.Ordinal));
            }
        }

        // Rows that merely restated the baseline carry no information under the new model — remove them,
        // along with the inert system_admin rows. What survives is exactly the tenant's additions.
        var redundant = legacyRows.Where(row =>
            string.Equals(row.Role, Roles.SystemAdmin, StringComparison.Ordinal)
            || (baseline.TryGetValue(row.Role, out var declared)
                && declared.Contains(row.Permission, StringComparer.Ordinal)));

        platform.RolePermissions.RemoveRange(redundant);
        platform.RolePermissionSuppressions.AddRange(suppressions);
        tenant.RolePermissionsConvertedAt = DateTimeOffset.UtcNow;

        try
        {
            // One transaction per tenant, so a reader never observes a half-converted tenant.
            await platform.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two instances starting against one database race here, and the loser hits the unique
            // index on (TenantId, Role, Permission). That is a peer having done the identical
            // deterministic work, not a failure — but only if the tenant really is converted now.
            // Anything else is a genuine error and must still fail startup.
            platform.ChangeTracker.Clear();
            var stamped = await platform.Tenants
                .AsNoTracking()
                .AnyAsync(t => t.Id == tenant.Id && t.RolePermissionsConvertedAt != null, cancellationToken);
            if (!stamped)
            {
                throw;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Role permission storage for tenant {Slug} was converted concurrently by another instance.",
                    tenant.Slug);
            }

            return false;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Converted role permission storage for tenant {Slug}: {Suppressions} suppression(s), " +
                "{Grants} tenant-specific grant(s) retained.",
                tenant.Slug, suppressions.Count, retainedGrants);
        }

        return true;
    }
}

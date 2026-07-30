using Plenipo.Application.Auditing;
using Plenipo.Application.Authorization;
using Plenipo.Application.Bootstrap;
using Plenipo.Application.Commerce;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Bootstrap;

/// <summary>
/// Creates the deployment's first tenant and operator from <see cref="BootstrapOptions"/>, exactly once.
/// Routed through <see cref="ITenantProvisioningService"/> rather than forking a second creation path —
/// that service already documents itself as working from any context including a startup scope with no
/// ambient tenant, and it commits in one transaction.
/// </summary>
public sealed class PlatformBootstrapper(
    PlatformDbContext db,
    ITenantProvisioningService provisioning,
    IOptions<BootstrapOptions> options,
    IOptions<AuthorizationSourceOptions> authorizationSource,
    IEnumerable<ProductRole> productRoles,
    IAuditLog auditLog,
    ILogger<PlatformBootstrapper> logger) : IPlatformBootstrapper
{
    public async Task<BootstrapResult> BootstrapAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            return new BootstrapResult(BootstrapOutcome.NotConfigured);
        }

        if (await HasOperatorPrincipalAsync(cancellationToken))
        {
            logger.LogInformation(
                "Bootstrap section present but the deployment already has an operator; nothing to do. " +
                "Remove the Bootstrap section from configuration.");
            return new BootstrapResult(BootstrapOutcome.AlreadyBootstrapped);
        }

        var slug = settings.TenantSlug!.Trim().ToLowerInvariant();
        var email = settings.AdminEmail!.Trim();
        var hasSubject = !string.IsNullOrWhiteSpace(settings.AdminSubject);
        var adminRoles = settings.EffectiveAdminRoles;
        var roleList = string.Join(", ", adminRoles);

        // In Token mode the IdP is the single authority for roles, so DB assignments are never consulted
        // (PermissionResolver skips them entirely). Create the tenant anyway — that half is still needed —
        // but say plainly that the roles will not take effect until the IdP asserts them.
        if (authorizationSource.Value.IsTokenSourced)
        {
            logger.LogWarning(
                "Auth:PermissionSource is Token, so Bootstrap:AdminRoles ({Roles}) will NOT grant anything: " +
                "the external IdP is the only source of roles. Configure the IdP to assert them for {Subject}.",
                roleList, settings.AdminSubject ?? email);
        }

        // With an explicit subject the roles bind to the identity the IdP will actually present. Without
        // one they bind through a standing invite instead, because TenantProvisioningService defaults a
        // missing subject to the EMAIL — which only matches an IdP that mints email-shaped subjects.
        // BootstrapOptions.ThrowIfInvalid already refuses this path for operator-grade roles.
        var result = await provisioning.ProvisionAsync(
            new ProvisionTenantCommand(
                Name: string.IsNullOrWhiteSpace(settings.TenantName) ? slug : settings.TenantName.Trim(),
                Slug: slug,
                AdminEmail: email,
                AdminSubject: settings.AdminSubject?.Trim(),
                AdminDisplayName: settings.AdminDisplayName?.Trim(),
                AdminRoles: hasSubject ? adminRoles : []),
            cancellationToken);

        if (result.Error == ProvisionError.SlugTaken)
        {
            // Another instance bootstrapped between the gate and here; the unique index on Slug arbitrated.
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Bootstrap lost the race for tenant {Slug}; another instance created it.", slug);
            }

            return new BootstrapResult(BootstrapOutcome.RaceLost, Slug: slug);
        }

        if (!result.Ok)
        {
            logger.LogError("Bootstrap could not create tenant {Slug}: {Detail}", slug, result.ErrorDetail);
            return new BootstrapResult(BootstrapOutcome.Failed, Slug: slug, Detail: result.ErrorDetail);
        }

        if (!hasSubject)
        {
            db.UserInvites.Add(new UserInvite
            {
                TenantId = result.TenantId,
                Email = email.ToLowerInvariant(),
                Roles = string.Join(",", adminRoles),
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await auditLog.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = result.TenantId,
            UserId = result.AdminUserId,
            Subject = Truncate(settings.AdminSubject ?? email, 200),
            UserDisplay = settings.AdminDisplayName,
            EventType = AuthAuditEventType.PlatformBootstrapped,
            Detail = Truncate(
                $"created first tenant '{slug}' with admin {email} holding {roleList}" +
                (hasSubject ? "" : " (via a standing invite, bound at first sign-in)"),
                1000),
        }, cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Bootstrapped tenant {Slug} with first admin {Email} holding {Roles}. " +
                "Remove the Bootstrap section from configuration now that the deployment has an operator.",
                slug, email, roleList);
        }

        return new BootstrapResult(BootstrapOutcome.Created, result.TenantId, slug);
    }

    /// <summary>
    /// THE GATE: does any principal anywhere in this deployment hold an operator-reserved permission?
    ///
    /// <para>Deliberately not "does a tenant exist". A tenant provisioned through commerce gets a
    /// <c>tenant_admin</c>, which lacks <c>platform.tenants.manage</c> by design — so keying on tenant
    /// existence would disarm bootstrap permanently while leaving nobody able to create a tenant. Asking
    /// the real question also re-arms correctly after a crash part-way through a bootstrap.</para>
    ///
    /// <para>These are the only queries in the platform that intentionally omit a <c>TenantId</c>
    /// predicate under <c>IgnoreQueryFilters</c>: the question is deployment-wide by definition, no row
    /// content is returned, and the startup scope has no ambient tenant to filter by.</para>
    ///
    /// <para>Roles are judged against the DECLARED baseline, ignoring per-tenant suppressions. That errs
    /// toward concluding an operator exists, which is the safe direction: a false "yes" leaves the
    /// operator to bootstrap by other means, while a false "no" would mint a second operator.</para>
    /// </summary>
    private async Task<bool> HasOperatorPrincipalAsync(CancellationToken cancellationToken)
    {
        if (await db.UserRoles.IgnoreQueryFilters().AnyAsync(r => r.Role == Roles.SystemAdmin, cancellationToken))
        {
            return true;
        }

        var directGrants = await db.UserPermissions.IgnoreQueryFilters()
            .Select(p => p.Permission).Distinct().ToListAsync(cancellationToken);
        if (GrantsOperatorPermission(directGrants))
        {
            return true;
        }

        var heldRoles = await db.UserRoles.IgnoreQueryFilters()
            .Select(r => r.Role).Distinct().ToListAsync(cancellationToken);
        var baseline = RoleBaseline.Merge(productRoles);

        return heldRoles.Any(role =>
            baseline.TryGetValue(role, out var permissions) && GrantsOperatorPermission(permissions));
    }

    private static bool GrantsOperatorPermission(IEnumerable<string> permissions)
    {
        var held = new HashSet<string>(permissions, StringComparer.Ordinal);
        return Permissions.OperatorOnly.Any(reserved => PermissionMatcher.IsGranted(held, reserved));
    }

    /// <summary>Audit columns are bounded; an over-long value would be dropped rather than truncated.</summary>
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

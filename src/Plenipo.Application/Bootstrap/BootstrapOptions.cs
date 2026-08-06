using System.Text.RegularExpressions;
using Plenipo.Application.Authorization;

namespace Plenipo.Application.Bootstrap;

/// <summary>
/// The first tenant and its first operator, supplied as configuration and consumed ONCE at startup.
///
/// <para>Outside Development the platform seeds nothing, so a fresh deployment starts with an empty
/// database — and because permissions are only resolved after a tenant resolves, no principal can hold
/// the permission needed to create one. That is a deadlock, not a permission problem, and it cannot be
/// unlocked over HTTP without opening a door that would then stand open forever.</para>
///
/// <para>So this is deliberately <b>not</b> an HTTP surface. An operator who can set configuration on the
/// deployment already controls it; asking them to say who the first admin is adds no authority they did
/// not have. The bootstrapper self-disarms the moment any principal in the deployment holds an
/// operator-only permission, and every run is audited as <c>PlatformBootstrapped</c>. Remove the section
/// after first run — the values are only read while the deployment has no operator.</para>
/// </summary>
public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    /// <summary>URL-safe tenant key, matched against <c>Tenant.Slug</c> and the token's tenant claim.</summary>
    public string? TenantSlug { get; set; }

    /// <summary>Display name for the tenant. Defaults to the slug.</summary>
    public string? TenantName { get; set; }

    /// <summary>The first admin's email address.</summary>
    public string? AdminEmail { get; set; }

    /// <summary>
    /// The first admin's stable IdP subject (<c>sub</c>). Optional, but REQUIRED when
    /// <see cref="AdminRoles"/> grants an operator-reserved permission: without it the roles are bound to
    /// an email, and the platform matches email from an unverified token claim.
    /// </summary>
    public string? AdminSubject { get; set; }

    public string? AdminDisplayName { get; set; }

    /// <summary>
    /// Local auth mode only (<c>Auth:Mode=Local</c>, ADR 0003): the first admin's initial password.
    /// Optional — when absent the platform generates a temporary one and prints it ONCE to the
    /// startup log. Either way the first sign-in forces a change, so the value in configuration (or
    /// in the log) stops working the moment it has done its one job. Ignored outside Local mode.
    /// </summary>
    public string? AdminInitialPassword { get; set; }

    /// <summary>
    /// Roles for the first admin, as configured. Empty means "unspecified" — see
    /// <see cref="EffectiveAdminRoles"/> for the default that then applies.
    ///
    /// <para>The backing array is deliberately EMPTY rather than pre-seeded with the default. .NET binds a
    /// configured array over the property's existing value index by index, so a default of
    /// <c>[system_admin]</c> plus an operator setting <c>Bootstrap__AdminRoles__0=tenant_admin</c> can
    /// leave <c>system_admin</c> in place — an operator who deliberately asked for a tenant-grade admin
    /// would silently get cross-tenant control. Defaulting at the point of use avoids that entirely.</para>
    ///
    /// <para>In environment-variable form each entry is its own key: <c>Bootstrap__AdminRoles__0</c>.</para>
    /// </summary>
    public string[] AdminRoles { get; set; } = [];

    /// <summary>
    /// The roles actually granted: what was configured, or <c>system_admin</c> when nothing was. An
    /// operator bootstrapping an empty deployment needs an operator — anything less and they bootstrap
    /// and are still locked out.
    /// </summary>
    public string[] EffectiveAdminRoles => AdminRoles.Length > 0 ? AdminRoles : [Roles.SystemAdmin];

    /// <summary>True when enough was supplied to attempt a bootstrap.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantSlug) && !string.IsNullOrWhiteSpace(AdminEmail);

    /// <summary>True when the section was PARTIALLY filled in — always an operator mistake, never a choice.</summary>
    public bool IsPartiallyConfigured =>
        !string.IsNullOrWhiteSpace(TenantSlug) ^ !string.IsNullOrWhiteSpace(AdminEmail);

    /// <summary>
    /// Fails fast on a section that cannot do what the operator intended. Called at registration, so a
    /// typo surfaces as a startup error rather than as a deployment that silently never bootstraps.
    /// </summary>
    /// <param name="declaredRoles">
    /// The effective role baseline (built-ins merged with host <c>ProductRole</c>s) — used to reject an
    /// unknown role name and to detect operator-reserved grants.
    /// </param>
    /// <param name="platformIssuesSubjects">
    /// True under <c>Auth:Mode=Local</c> (ADR 0003), where the platform mints the admin's subject
    /// itself and binds roles to it directly — no unverified email matching is involved, so the
    /// "operator roles need an explicit subject" guard does not apply.
    /// </param>
    public void ThrowIfInvalid(IReadOnlyDictionary<string, string[]> declaredRoles, bool platformIssuesSubjects = false)
    {
        ArgumentNullException.ThrowIfNull(declaredRoles);

        if (IsPartiallyConfigured)
        {
            throw new InvalidOperationException(
                "Bootstrap is half-configured: set BOTH Bootstrap:TenantSlug and Bootstrap:AdminEmail, or neither.");
        }

        if (!IsConfigured)
        {
            return;
        }

        if (!Regex.IsMatch(TenantSlug!, "^[a-z0-9][a-z0-9-]{0,62}$"))
        {
            throw new InvalidOperationException(
                $"Bootstrap:TenantSlug '{TenantSlug}' must be lowercase letters, digits, and hyphens (max 63 chars).");
        }

        // The column is varchar(320); a longer value would be truncated or rejected at write time, long
        // after the operator could connect the failure to what they typed.
        if (!AdminEmail!.Contains('@', StringComparison.Ordinal) || AdminEmail.Length > 320)
        {
            throw new InvalidOperationException(
                "Bootstrap:AdminEmail must be a valid email address of at most 320 characters.");
        }

        if (AdminSubject is { Length: > 200 })
        {
            throw new InvalidOperationException("Bootstrap:AdminSubject must be at most 200 characters.");
        }

        // An entry that trims to nothing is a typo, not a choice: it would silently shrink the set.
        if (AdminRoles.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "Bootstrap:AdminRoles contains an empty entry; a bootstrapped admin's roles must all be named.");
        }

        foreach (var role in EffectiveAdminRoles)
        {
            if (!Roles.All.Contains(role, StringComparer.Ordinal) && !declaredRoles.ContainsKey(role))
            {
                throw new InvalidOperationException(
                    $"Bootstrap:AdminRoles contains unknown role '{role}'. Use a built-in role or one the host declares with AddPlenipoRole.");
            }
        }

        // An email-keyed bootstrap binds roles through a standing invite, which RequestEnricher matches
        // against the token's email claim — and email is not a verified identifier. Fine for tenant-grade
        // roles; not fine for handing out cross-tenant control to whoever presents the address first.
        // Not applicable when the platform issues subjects itself (Local mode): there the roles bind to a
        // platform-minted subject whose only credential is the one bootstrap creates.
        if (!platformIssuesSubjects
            && string.IsNullOrWhiteSpace(AdminSubject)
            && EffectiveAdminRoles.Any(r => GrantsOperatorPermission(r, declaredRoles)))
        {
            throw new InvalidOperationException(
                "Bootstrap:AdminSubject is required when Bootstrap:AdminRoles grants operator-reserved permissions; " +
                "an email-keyed invite is matched against an unverified claim. Set the IdP subject (`sub`) of the first operator.");
        }

        // Too short to be a real choice; catch it before it becomes the deployment's operator password.
        if (AdminInitialPassword is { Length: > 0 and < 12 })
        {
            throw new InvalidOperationException(
                "Bootstrap:AdminInitialPassword must be at least 12 characters (or omitted, to have one generated).");
        }
    }

    private static bool GrantsOperatorPermission(string role, IReadOnlyDictionary<string, string[]> declaredRoles)
    {
        if (string.Equals(role, Roles.SystemAdmin, StringComparison.Ordinal))
        {
            return true; // the global wildcard covers every operator-only permission by construction
        }

        return declaredRoles.TryGetValue(role, out var permissions)
            && Permissions.OperatorOnly.Any(reserved =>
                PermissionMatcher.IsGranted(new HashSet<string>(permissions, StringComparer.Ordinal), reserved));
    }
}

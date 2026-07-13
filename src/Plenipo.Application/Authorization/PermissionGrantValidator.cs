namespace Plenipo.Application.Authorization;

/// <summary>
/// Validates the permission strings an admin assigns to a role or user against the deployment's known
/// permissions — the built-in platform permissions plus every registered module tool. Modules are registered
/// at startup, so this set is fixed: a grant that matches nothing is a typo that would be persisted as a dead
/// permission (it silently grants nothing, and no error ever surfaces). Rejecting it with feedback is strictly
/// better than storing it.
/// </summary>
/// <remarks>
/// A grant is considered meaningful when it would satisfy at least one known permission under the same
/// <see cref="PermissionMatcher"/> rules the runtime enforces — so a concrete permission must exist, and a
/// wildcard (e.g. <c>platform.*</c>) must cover at least one real permission. Whether a role <em>should</em> be
/// allowed to hold a broad wildcard is a separate policy question; this only rejects grants that match nothing.
/// </remarks>
public static class PermissionGrantValidator
{
    /// <summary>
    /// The full set of concrete permissions this deployment recognises: the platform permissions plus the
    /// supplied module tool permissions (typically <c>catalog.Manifests.SelectMany(m =&gt; m.Tools)…</c>).
    /// </summary>
    public static IReadOnlySet<string> KnownPermissions(IEnumerable<string> moduleToolPermissions)
    {
        ArgumentNullException.ThrowIfNull(moduleToolPermissions);

        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var platform in PermissionCatalog.Platform)
        {
            known.Add(platform.Permission);
        }
        foreach (var permission in moduleToolPermissions)
        {
            if (!string.IsNullOrWhiteSpace(permission))
            {
                known.Add(permission);
            }
        }
        return known;
    }

    /// <summary>
    /// Returns the grants that match no known permission (neither exactly nor via a wildcard), preserving order
    /// and de-duplicated. An empty result means every grant is meaningful. Blank grants are ignored (the write
    /// endpoints already strip them).
    /// </summary>
    public static IReadOnlyList<string> FindUnknownGrants(IEnumerable<string?> grants, IReadOnlySet<string> knownPermissions)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(knownPermissions);

        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (string.IsNullOrWhiteSpace(grant) || !seen.Add(grant))
            {
                continue;
            }
            if (!IsMeaningful(grant, knownPermissions))
            {
                unknown.Add(grant);
            }
        }
        return unknown;
    }

    /// <summary>
    /// Returns the grants that would confer an operator-reserved permission the caller does not itself hold —
    /// a tenant-scoped admin must not grant (nor a custom role hold) cross-tenant capabilities such as
    /// <see cref="Permissions.ManageTenants"/>, whether named directly or via a covering wildcard
    /// (<c>platform.*</c>, <c>*</c>). Order-preserving and de-duplicated; empty when every grant is allowed.
    /// The <paramref name="callerHolds"/> predicate is the escalation guard: a caller may only hand out an
    /// operator-reserved capability it already possesses (so the operator can still delegate).
    /// </summary>
    public static IReadOnlyList<string> FindForbiddenGrants(
        IEnumerable<string?> grants,
        IReadOnlyCollection<string> operatorOnlyPermissions,
        Func<string, bool> callerHolds)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(operatorOnlyPermissions);
        ArgumentNullException.ThrowIfNull(callerHolds);

        var forbidden = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (string.IsNullOrWhiteSpace(grant) || !seen.Add(grant))
            {
                continue;
            }

            var held = new HashSet<string>(StringComparer.Ordinal) { grant };
            foreach (var reserved in operatorOnlyPermissions)
            {
                if (PermissionMatcher.IsGranted(held, reserved) && !callerHolds(reserved))
                {
                    forbidden.Add(grant);
                    break;
                }
            }
        }
        return forbidden;
    }

    private static bool IsMeaningful(string grant, IReadOnlySet<string> knownPermissions)
    {
        // Use the runtime matcher so validation and enforcement agree exactly: the grant is meaningful iff,
        // treated as the sole held grant, it would satisfy at least one known permission.
        var held = new HashSet<string>(StringComparer.Ordinal) { grant };
        foreach (var known in knownPermissions)
        {
            if (PermissionMatcher.IsGranted(held, known))
            {
                return true;
            }
        }
        return false;
    }
}

namespace Plenipo.Application.Authorization;

/// <summary>
/// The deployment's authentication mode, bound from the "Auth" section (alongside the OIDC settings
/// and <see cref="AuthorizationSourceOptions"/>). Application-layer on purpose: infrastructure
/// services (bootstrap, credential service) branch on it and must not reach up into the host layer.
/// <list type="bullet">
///   <item><b>unset</b> (default) — behavior is exactly what it always was: a configured
///   Authority/Audience means external OIDC; nothing configured means the Development-only header
///   fallback, or a startup error outside Development.</item>
///   <item><b>Oidc</b> — the implicit "external authority" choice made explicit. Requires
///   Authority + Audience.</item>
///   <item><b>Local</b> — the host is its own OpenID Connect issuer (ADR 0003): built-in login page,
///   credentials in the platform database, no external IdP. Explicit opt-in only, so the
///   fail-fast-when-unconfigured default never silently weakens.</item>
/// </list>
/// </summary>
public sealed class AuthModeOptions
{
    public const string SectionName = "Auth";

    public const string Local = "Local";
    public const string Oidc = "Oidc";

    /// <summary>Empty/unset (auto), "Oidc", or "Local".</summary>
    public string? Mode { get; set; }

    public bool IsLocal => string.Equals(Mode, Local, StringComparison.OrdinalIgnoreCase);

    public bool IsExplicitOidc => string.Equals(Mode, Oidc, StringComparison.OrdinalIgnoreCase);

    public void ThrowIfInvalid(AuthorizationSourceOptions authorizationSource)
    {
        ArgumentNullException.ThrowIfNull(authorizationSource);

        if (!string.IsNullOrWhiteSpace(Mode) && !IsLocal && !IsExplicitOidc)
        {
            throw new InvalidOperationException(
                $"Auth:Mode '{Mode}' is not supported. Use \"Local\" (built-in sign-in), \"Oidc\" (external identity provider), or leave it unset.");
        }

        // In Local mode the only role authority IS this database; delegating authorization to an
        // external IdP that doesn't exist is a contradiction, not a configuration.
        if (IsLocal && authorizationSource.IsTokenSourced)
        {
            throw new InvalidOperationException(
                "Auth:Mode=Local requires Auth:PermissionSource=Database: the embedded issuer has no external IdP to source roles from.");
        }
    }
}

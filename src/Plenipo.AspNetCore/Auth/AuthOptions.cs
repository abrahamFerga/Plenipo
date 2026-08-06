namespace Plenipo.AspNetCore.Auth;

/// <summary>JWT / OIDC settings, bound from the "Auth" section. In production these point at Entra External ID.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Unset (auto: a configured Authority means external OIDC, else the Development fallback),
    /// <c>Oidc</c> (the external-authority choice made explicit), or <c>Local</c> — the host is its
    /// own OpenID Connect issuer with a built-in login page and credentials in the platform database
    /// (ADR 0003). <c>Local</c> is explicit opt-in only, so the fail-fast-when-unconfigured default
    /// never silently weakens. Mirrors <c>AuthModeOptions</c>, which binds the same section for the
    /// infrastructure layer.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>OIDC authority (e.g. https://&lt;tenant&gt;.ciamlogin.com/&lt;tenant-id&gt;/v2.0). Empty disables JWT validation.</summary>
    public string? Authority { get; set; }

    /// <summary>Expected audience (the API's application/client id).</summary>
    public string? Audience { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// The PUBLIC client id the browser and mobile apps sign in with. Published unauthenticated by
    /// <c>GET /api/platform/auth-config</c> so one prebuilt bundle can serve every deployment — the shell
    /// asks the host who to authenticate against instead of baking it in at build time.
    ///
    /// <para>Deliberately NOT part of <see cref="IsConfigured"/>: an existing API-only deployment must keep
    /// starting after an upgrade without adding config it never needed. A browser client that finds no
    /// client id says so on screen instead.</para>
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Space-separated scopes the browser requests, beyond <c>openid profile email</c>. Empty by default:
    /// the correct value is IdP-specific (Entra wants <c>{audience}/.default</c>, Keycloak and Authentik
    /// reject it), so the platform ships no guess.
    /// </summary>
    public string? Scopes { get; set; }

    /// <summary>Claim whose value identifies the Plenipo tenant (matched against <c>Tenant.Slug</c>).</summary>
    public string TenantClaim { get; set; } = "tenant";

    /// <summary>
    /// Reject tokens that were not issued after multi-factor authentication (judged by the token's
    /// <c>amr</c> claim against <see cref="MfaAmrValues"/>). Plenipo deliberately has no credential
    /// store — enrollment of TOTP/passkeys happens at the IdP (Entra External ID, Keycloak, …);
    /// this switch is the platform-side backstop so a misconfigured IdP can't silently admit
    /// single-factor sessions. Applies only to JWT bearer auth; the Development-only dev-auth
    /// fallback is unaffected.
    /// </summary>
    public bool RequireMfa { get; set; }

    /// <summary>
    /// <c>amr</c> values accepted as proof of MFA. Defaults cover Entra's markers (mfa, ngcmfa),
    /// FIDO2/passkeys (fido), one-time codes (otp), and hardware keys (hwk).
    /// </summary>
    public string[] MfaAmrValues { get; set; } = ["mfa", "ngcmfa", "fido", "otp", "hwk"];

    /// <summary>JWT authentication is configured only when both issuer and resource audience are pinned.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(Audience);

    /// <summary>True when a partial JWT configuration was supplied and must fail fast.</summary>
    public bool IsPartiallyConfigured =>
        !string.IsNullOrWhiteSpace(Authority) ^ !string.IsNullOrWhiteSpace(Audience);

    /// <summary>True when the host runs as its own issuer (<c>Auth:Mode=Local</c>, ADR 0003).</summary>
    public bool IsLocalMode => string.Equals(Mode, "Local", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the operator explicitly pinned external OIDC (<c>Auth:Mode=Oidc</c>).</summary>
    public bool IsExplicitOidcMode => string.Equals(Mode, "Oidc", StringComparison.OrdinalIgnoreCase);
}

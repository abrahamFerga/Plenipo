namespace Cortex.AspNetCore.Auth;

/// <summary>JWT / OIDC settings, bound from the "Auth" section. In production these point at Entra External ID.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>OIDC authority (e.g. https://&lt;tenant&gt;.ciamlogin.com/&lt;tenant-id&gt;/v2.0). Empty disables JWT validation.</summary>
    public string? Authority { get; set; }

    /// <summary>Expected audience (the API's application/client id).</summary>
    public string? Audience { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Claim whose value identifies the Cortex tenant (matched against <c>Tenant.Slug</c>).</summary>
    public string TenantClaim { get; set; } = "tenant";

    /// <summary>
    /// Reject tokens that were not issued after multi-factor authentication (judged by the token's
    /// <c>amr</c> claim against <see cref="MfaAmrValues"/>). Cortex deliberately has no credential
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
}

namespace Plenipo.AspNetCore.Auth.Local;

/// <summary>Fixed names and paths of the embedded issuer (Auth:Mode=Local, ADR 0003).</summary>
public static class LocalAuthDefaults
{
    /// <summary>
    /// The built-in public client the browser shells sign in with. Published by
    /// <c>/api/platform/auth-config</c> (overridable via <c>Auth:ClientId</c>).
    /// </summary>
    public const string ClientId = "plenipo-web";

    /// <summary>The <c>aud</c> pinned into locally issued access tokens and required by validation.</summary>
    public const string Audience = "plenipo";

    /// <summary>The cookie scheme that authenticates ONLY the issuer surface (login + authorize).</summary>
    public const string CookieScheme = "PlenipoLocal";

    public const string LoginPath = "/auth/login";

    public const string AuthorizeEndpoint = "/connect/authorize";
    public const string TokenEndpoint = "/connect/token";
    public const string EndSessionEndpoint = "/connect/logout";

    /// <summary>
    /// The only redirect paths the issuer will send a code to — matched on the REQUESTING host,
    /// because a mini PC is legitimately reached by hostname, mDNS name, and LAN IP at once
    /// (see <c>LocalApplicationManager</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> CallbackPaths = ["/signin-callback", "/admin/signin-callback"];

    /// <summary>Claim carrying the platform user id, so token-time checks never guess from <c>sub</c>.</summary>
    public const string UserIdClaim = "plenipo_uid";

    /// <summary>
    /// Claim carrying the credential's security stamp. Compared against the live row on refresh, so a
    /// password change or admin reset ends every session the old password minted.
    /// </summary>
    public const string StampClaim = "plenipo_stamp";
}

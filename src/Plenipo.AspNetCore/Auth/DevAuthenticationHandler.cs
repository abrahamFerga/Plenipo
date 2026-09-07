using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Plenipo.AspNetCore.Auth;

/// <summary>
/// Development-only authentication: turns optional <c>X-Dev-*</c> headers (or sensible defaults) into an
/// authenticated principal so the platform is fully exercisable without standing up an identity provider.
/// Registered ONLY when no real authority is configured and the environment is Development.
/// </summary>
public sealed class DevAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";

    /// <summary>
    /// The roles an ABSENT <c>X-Dev-Roles</c> header asserts. Unset: <c>system_admin</c>, the
    /// development convenience that makes a fresh clone fully operable with no headers at all. A
    /// product sets it to an empty string so that absence means <em>no roles</em> — because an absent
    /// header is exactly what a header-stripping proxy produces, and a proxy must be able to degrade a
    /// caller, never escalate one (#167). A present-but-empty header always means no roles.
    /// </summary>
    public const string RolesWhenAbsentKey = "Auth:Dev:RolesWhenAbsent";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Value("X-Dev-Subject", "dev-user");
        var email = Value("X-Dev-Email", "dev@plenipo.local");
        var name = Value("X-Dev-Name", "Dev User");
        var tenant = Value("X-Dev-Tenant", "dev");
        // Roles: an ABSENT value defaults to Auth:Dev:RolesWhenAbsent (system_admin unless the product
        // says otherwise); a PRESENT-but-empty one is an explicitly role-less token — how a real IdP
        // presents an unscoped principal (exercises the Auth:DefaultRole JIT path). The query fallback
        // must preserve that asymmetry: collapsing the two would hand system_admin to a caller who
        // asked for nothing.
        var rawRoles = Request.Headers.TryGetValue("X-Dev-Roles", out var rolesHeader)
            ? rolesHeader.ToString()
            : IsHubPath && Request.Query.TryGetValue("X-Dev-Roles", out var rolesQuery)
                ? rolesQuery.ToString()
                : null;
        var roles = rawRoles is null
            ? RolesWhenAbsent()
            : rawRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subject),
            new("sub", subject),
            new(ClaimTypes.Email, email),
            new("name", name),
            new("tenant", tenant),
        };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));

        var identity = new ClaimsIdentity(claims, SchemeName, "name", "roles");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string[] RolesWhenAbsent()
    {
        var configured = configuration[RolesWhenAbsentKey];
        return configured is null
            ? ["system_admin"]
            : configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// A browser's WebSocket handshake cannot set request headers, so SignalR can only carry the caller's
    /// dev identity in the query string. Accept it for HUB PATHS ONLY — the same restriction, for the same
    /// reason, that <c>AuthSetup</c> puts on the JwtBearer <c>access_token</c> parameter: a query string is
    /// kept in browser history and written to proxy logs, so widening this to the REST surface would put
    /// identity where headers never reach, and the REST surface can carry headers perfectly well anyway.
    /// <para>
    /// Without this, every <c>/hubs</c> turn in Development authenticates as the fallbacks below —
    /// tenant <c>dev</c>, roles <c>system_admin</c> ⇒ <c>["*"]</c> — so the pre-model-call tool filter
    /// offers tools RBAC should have removed and approvals park in a tenant nobody addressed.
    /// </para>
    /// </summary>
    private bool IsHubPath => Request.Path.StartsWithSegments("/hubs", StringComparison.Ordinal);

    /// <summary>Header first, then the hub-path query fallback, then the development default.</summary>
    private string Value(string key, string fallback) =>
        Request.Headers.TryGetValue(key, out var header) && !string.IsNullOrWhiteSpace(header)
            ? header.ToString()
            : IsHubPath && Request.Query.TryGetValue(key, out var query) && !string.IsNullOrWhiteSpace(query)
                ? query.ToString()
                : fallback;
}

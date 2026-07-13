using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Application.Connectors;
using Cortex.Application.Security;
using Cortex.Connectors.Sdk;
using Cortex.Core.Identity;
using Cortex.Infrastructure.Connectors;
using Cortex.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Cortex.AspNetCore.Endpoints;

/// <summary>
/// Stage 2 of delegated-connector enablement: each user connects their OWN account. `start`
/// builds the IdP authorize URL (auth-code + PKCE; the verifier travels data-protected inside
/// `state`, never to the browser in the clear); `callback` exchanges the code and stores the
/// user's tokens protected. The connector must be tenant-enabled first — stage 1 gates stage 2.
/// The list/disconnect pair is the end-user "connected accounts" surface: any authenticated user
/// sees which delegated connectors they can link and manages ONLY their own login.
/// </summary>
public static class ConnectorOAuthEndpoints
{
    private const string StatePurpose = "Cortex.Connectors.OAuthState";

    public static void MapConnectorOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/connectors").WithTags("Connectors").RequireAuthorization();

        // The tenant-enabled DELEGATED connectors with whether the CALLER has linked their account.
        // Service-mode connectors never appear: there is nothing for an individual user to connect.
        group.MapGet("/", async (
                IConnectorCatalog catalog, PlatformDbContext db, ICurrentUser current,
                CancellationToken cancellationToken) =>
            {
                if (current.UserId is not Guid userId)
                {
                    return Results.Unauthorized();
                }

                var enabled = await db.TenantConnectors
                    .Where(c => c.Enabled)
                    .Select(c => c.ConnectorId)
                    .ToListAsync(cancellationToken);
                var mine = await db.UserConnectorLogins
                    .Where(l => l.UserId == userId)
                    .Select(l => l.ConnectorId)
                    .ToListAsync(cancellationToken);

                var result = catalog.Manifests
                    .Where(m => m.AuthMode == ConnectorAuthMode.UserDelegated && enabled.Contains(m.Id))
                    .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new UserConnectorDto(
                        m.Id, m.DisplayName, m.Description, m.Icon, Connected: mine.Contains(m.Id)));
                return Results.Ok(result);
            })
            .WithName("Connectors_ListForUser");

        // Unlink the CALLER's account. The stored tokens are the row — deleting it revokes our
        // copy; the IdP-side grant is the user's to manage in their provider account.
        group.MapDelete("/{connectorId}/login", async (
                string connectorId, PlatformDbContext db, ICurrentUser current,
                CancellationToken cancellationToken) =>
            {
                if (current.UserId is not Guid userId)
                {
                    return Results.Unauthorized();
                }

                var row = await db.UserConnectorLogins
                    .FirstOrDefaultAsync(l => l.ConnectorId == connectorId && l.UserId == userId, cancellationToken);
                if (row is null)
                {
                    return Results.NotFound();
                }

                db.UserConnectorLogins.Remove(row);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .WithName("Connectors_DisconnectLogin");

        group.MapGet("/{connectorId}/oauth/start", async (
                string connectorId, HttpContext http,
                IConnectorCatalog catalog, ITenantConnectorStore store,
                ConnectorUserLoginService logins, IDataProtectionProvider dataProtection,
                ICurrentUser current, OutboundUrlPolicy outboundUrls,
                CancellationToken cancellationToken) =>
            {
                if (current.UserId is not Guid userId || current.TenantId is not Guid tenantId)
                {
                    return Results.Unauthorized();
                }
                if (!catalog.TryGetManifest(connectorId, out var manifest) || manifest is null ||
                    manifest.AuthMode != ConnectorAuthMode.UserDelegated)
                {
                    return Results.NotFound();
                }

                if (!await store.IsEnabledAsync(connectorId, cancellationToken))
                {
                    return Results.Conflict(new { error = $"The '{connectorId}' connector is not enabled for this tenant." });
                }

                var values = await ((IConnectorSettings)http.RequestServices.GetRequiredService<ConnectorSettingsService>())
                    .GetAsync(connectorId, cancellationToken);
                var needsAuthority = manifest.OAuthAuthorizeUrlTemplate.Contains("{authority}", StringComparison.Ordinal);
                string? authority = null;
                if (values is null || !values.TryGetValue("ClientId", out var clientId) || string.IsNullOrWhiteSpace(clientId) ||
                    (needsAuthority && (!values.TryGetValue("Authority", out authority) || string.IsNullOrWhiteSpace(authority))))
                {
                    return Results.Conflict(new { error = "The connector's ClientId (and Authority, where the IdP needs one) settings are not configured." });
                }

                var config = await logins.ResolveOAuthConfigAsync(connectorId, cancellationToken);
                var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var state = dataProtection.CreateProtector(StatePurpose)
                    .ToTimeLimitedDataProtector()
                    .Protect(
                        JsonSerializer.Serialize(new OAuthState(connectorId, verifier, tenantId, userId)),
                        lifetime: TimeSpan.FromMinutes(10));

                var redirectUri = $"{http.Request.Scheme}://{http.Request.Host}/api/connectors/{connectorId}/oauth/callback";
                // The manifest's template decides the IdP's URL shape (Entra path, Google's fixed
                // URL with extra params, …); we only append the standard auth-code+PKCE parameters.
                var authorizeBase = manifest.OAuthAuthorizeUrlTemplate
                    .Replace("{authority}", authority?.TrimEnd('/'), StringComparison.Ordinal);
                try
                {
                    await outboundUrls.RequireAllowedAsync(authorizeBase, cancellationToken);
                }
                catch (ArgumentException ex)
                {
                    return Results.Conflict(new { error = $"The connector authorization endpoint is not allowed: {ex.Message}" });
                }
                var separator = authorizeBase.Contains('?') ? '&' : '?';
                var authorizeUrl = authorizeBase +
                    $"{separator}client_id={Uri.EscapeDataString(clientId)}" +
                    "&response_type=code" +
                    $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                    $"&scope={Uri.EscapeDataString(config?.Scopes ?? "offline_access")}" +
                    $"&state={Uri.EscapeDataString(state)}" +
                    $"&code_challenge={challenge}&code_challenge_method=S256";

                return Results.Ok(new { authorizeUrl });
            })
            .WithName("Connectors_OAuthStart");

        group.MapGet("/{connectorId}/oauth/callback", async (
                string connectorId, string code, string state, HttpContext http,
                ConnectorUserLoginService logins, IOAuthTokenClient oauth,
                IDataProtectionProvider dataProtection, ICurrentUser current, OutboundUrlPolicy outboundUrls,
                CancellationToken cancellationToken) =>
            {
                OAuthState? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<OAuthState>(
                        dataProtection.CreateProtector(StatePurpose)
                            .ToTimeLimitedDataProtector()
                            .Unprotect(state));
                }
                catch (CryptographicException)
                {
                    return Results.BadRequest(new { error = "Invalid or expired state." });
                }

                if (payload is null ||
                    !string.Equals(payload.ConnectorId, connectorId, StringComparison.Ordinal) ||
                    payload.TenantId != current.TenantId || payload.UserId != current.UserId)
                {
                    return Results.BadRequest(new { error = "State does not match this connector or signed-in user." });
                }

                var config = await logins.ResolveOAuthConfigAsync(connectorId, cancellationToken);
                if (config is null)
                {
                    return Results.Conflict(new { error = "The connector is not configured for this tenant." });
                }

                try
                {
                    await outboundUrls.RequireAllowedAsync(config.TokenEndpoint, cancellationToken);
                }
                catch (ArgumentException ex)
                {
                    return Results.Conflict(new { error = $"The connector token endpoint is not allowed: {ex.Message}" });
                }

                var redirectUri = $"{http.Request.Scheme}://{http.Request.Host}/api/connectors/{connectorId}/oauth/callback";
                var tokens = await oauth.ExchangeCodeAsync(config, code, redirectUri, payload.CodeVerifier, cancellationToken);
                await logins.StoreAsync(connectorId, tokens, cancellationToken);

                return Results.Text($"Connected. You can close this window and return to Cortex — the '{connectorId}' connector is now linked to your account.");
            })
            .WithName("Connectors_OAuthCallback");
    }

    private sealed record OAuthState(string ConnectorId, string CodeVerifier, Guid TenantId, Guid UserId);

    private sealed record UserConnectorDto(
        string Id, string DisplayName, string Description, string? Icon, bool Connected);
}

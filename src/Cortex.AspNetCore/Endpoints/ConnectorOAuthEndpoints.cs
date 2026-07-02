using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortex.Application.Connectors;
using Cortex.Connectors.Sdk;
using Cortex.Infrastructure.Connectors;
using Microsoft.AspNetCore.DataProtection;

namespace Cortex.AspNetCore.Endpoints;

/// <summary>
/// Stage 2 of delegated-connector enablement: each user connects their OWN account. `start`
/// builds the IdP authorize URL (auth-code + PKCE; the verifier travels data-protected inside
/// `state`, never to the browser in the clear); `callback` exchanges the code and stores the
/// user's tokens protected. The connector must be tenant-enabled first — stage 1 gates stage 2.
/// </summary>
public static class ConnectorOAuthEndpoints
{
    private const string StatePurpose = "Cortex.Connectors.OAuthState";

    public static void MapConnectorOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/connectors").WithTags("Connectors").RequireAuthorization();

        group.MapGet("/{connectorId}/oauth/start", async (
                string connectorId, HttpContext http,
                IConnectorCatalog catalog, ITenantConnectorStore store,
                ConnectorUserLoginService logins, IDataProtectionProvider dataProtection,
                CancellationToken cancellationToken) =>
            {
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
                if (values is null || !values.TryGetValue("Authority", out var authority) ||
                    !values.TryGetValue("ClientId", out var clientId))
                {
                    return Results.Conflict(new { error = "The connector's Authority/ClientId settings are not configured." });
                }

                var config = await logins.ResolveOAuthConfigAsync(connectorId, cancellationToken);
                var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                var state = dataProtection.CreateProtector(StatePurpose)
                    .Protect(JsonSerializer.Serialize(new OAuthState(connectorId, verifier)));

                var redirectUri = $"{http.Request.Scheme}://{http.Request.Host}/api/connectors/{connectorId}/oauth/callback";
                var authorizeUrl = $"{authority.TrimEnd('/')}/oauth2/v2.0/authorize" +
                    $"?client_id={Uri.EscapeDataString(clientId)}" +
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
                IDataProtectionProvider dataProtection, CancellationToken cancellationToken) =>
            {
                OAuthState? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<OAuthState>(
                        dataProtection.CreateProtector(StatePurpose).Unprotect(state));
                }
                catch (CryptographicException)
                {
                    return Results.BadRequest(new { error = "Invalid or expired state." });
                }

                if (payload is null || !string.Equals(payload.ConnectorId, connectorId, StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { error = "State does not match this connector." });
                }

                var config = await logins.ResolveOAuthConfigAsync(connectorId, cancellationToken);
                if (config is null)
                {
                    return Results.Conflict(new { error = "The connector is not configured for this tenant." });
                }

                var redirectUri = $"{http.Request.Scheme}://{http.Request.Host}/api/connectors/{connectorId}/oauth/callback";
                var tokens = await oauth.ExchangeCodeAsync(config, code, redirectUri, payload.CodeVerifier, cancellationToken);
                await logins.StoreAsync(connectorId, tokens, cancellationToken);

                return Results.Text($"Connected. You can close this window and return to Cortex — the '{connectorId}' connector is now linked to your account.");
            })
            .WithName("Connectors_OAuthCallback");
    }

    private sealed record OAuthState(string ConnectorId, string CodeVerifier);
}

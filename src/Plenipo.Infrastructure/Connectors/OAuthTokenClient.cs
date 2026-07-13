using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Connectors;

namespace Plenipo.Infrastructure.Connectors;

/// <summary>
/// The real authorization-code + PKCE exchange (standard OAuth2 form posts — works against Entra,
/// B2C, or any compliant IdP). Tests replace <see cref="IOAuthTokenClient"/> with a fake; nothing
/// here is Microsoft-specific beyond the defaults the connectors choose.
/// </summary>
public sealed class OAuthTokenClient(IHttpClientFactory httpClients) : IOAuthTokenClient
{
    public const string HttpClientName = "connector-oauth";

    public async Task<OAuthTokens> ExchangeCodeAsync(
        OAuthClientConfig config, string code, string redirectUri, string codeVerifier,
        CancellationToken cancellationToken = default) =>
        await PostAsync(config, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
        }, cancellationToken)
        ?? throw new InvalidOperationException("The identity provider rejected the authorization code exchange.");

    public Task<OAuthTokens?> RefreshAsync(
        OAuthClientConfig config, string refreshToken, CancellationToken cancellationToken = default) =>
        PostAsync(config, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, cancellationToken);

    private async Task<OAuthTokens?> PostAsync(
        OAuthClientConfig config, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        form["client_id"] = config.ClientId;
        form["scope"] = config.Scopes;
        if (!string.IsNullOrWhiteSpace(config.ClientSecret))
        {
            form["client_secret"] = config.ClientSecret;
        }

        var client = httpClients.CreateClient(HttpClientName);
        using var response = await client.PostAsync(
            config.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var access = json.GetProperty("access_token").GetString()!;
        var refresh = json.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        var expiresIn = json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        return new OAuthTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }
}

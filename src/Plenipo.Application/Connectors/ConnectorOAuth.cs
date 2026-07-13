namespace Plenipo.Application.Connectors;

/// <summary>An OAuth client's coordinates, resolved from a delegated connector's tenant settings.</summary>
public sealed record OAuthClientConfig(string TokenEndpoint, string ClientId, string? ClientSecret, string Scopes);

/// <summary>Tokens returned by the IdP's token endpoint.</summary>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// The authorization-code + PKCE exchange against the IdP — a seam so keyless tests fake the IdP
/// while production talks to login.microsoftonline.com (or any OAuth2 endpoint).
/// </summary>
public interface IOAuthTokenClient
{
    public Task<OAuthTokens> ExchangeCodeAsync(
        OAuthClientConfig config, string code, string redirectUri, string codeVerifier,
        CancellationToken cancellationToken = default);

    public Task<OAuthTokens?> RefreshAsync(
        OAuthClientConfig config, string refreshToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// How a delegated connector's tools get the CURRENT user's access token: null means "not
/// connected" (the tool answers with the connect link — /api/connectors/{id}/oauth/start), and
/// expired tokens refresh transparently when a refresh token exists.
/// </summary>
public interface IConnectorUserLogins
{
    public Task<string?> GetAccessTokenAsync(string connectorId, CancellationToken cancellationToken = default);
}

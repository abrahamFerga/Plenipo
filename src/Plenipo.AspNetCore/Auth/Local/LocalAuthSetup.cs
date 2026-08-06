using Plenipo.Infrastructure.LocalAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Plenipo.AspNetCore.Auth.Local;

/// <summary>
/// Wires the embedded issuer (Auth:Mode=Local, ADR 0003): the OpenIddict authorization server, the
/// cookie scheme that authenticates ONLY the issuer's own login/authorize surface, and JwtBearer
/// validation of the tokens it mints — so everything past authentication (enrichment, RBAC, SignalR,
/// the MFA backstop) runs the identical code path as an external-IdP deployment.
/// </summary>
public static class LocalAuthSetup
{
    internal static void AddLocalAuth(
        AuthenticationBuilder authBuilder, IServiceCollection services, AuthOptions auth)
    {
        // ── API side: the same JwtBearer scheme external OIDC uses, fed by the local signing key. ──
        authBuilder.AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                // The per-deployment signing key — generated on first boot, never shared — is the
                // trust anchor. An on-prem host is legitimately reached by hostname AND by LAN IP, so
                // the issuer string varies with the URL the browser used; pinning one would break the
                // other path while proving nothing the signature doesn't already prove.
                ValidateIssuer = false,
                ValidateAudience = true,
                ValidAudience = LocalAuthDefaults.Audience,
                ValidateLifetime = true,
                NameClaimType = "name",
                RoleClaimType = "roles",
                // Only OpenIddict ACCESS tokens ("at+jwt"). An id_token is signed with the same key;
                // its different audience would already reject it, but the typ pin makes the class of
                // confusion impossible rather than merely caught.
                ValidTypes = ["at+jwt"],
            };
            options.Events = new JwtBearerEvents();
            AuthSetup.AttachSharedJwtBearerEvents(options, auth);
        });

        // The signing key lives in the database and is loaded by DatabaseInitializer before the host
        // serves traffic; a RESOLVER (invoked per validation) keeps options construction from touching
        // it earlier than that.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<ILocalAuthKeyRing>((options, keyRing) =>
                options.TokenValidationParameters.IssuerSigningKeyResolver = (_, _, _, _) =>
                    [new RsaSecurityKey(keyRing.SigningKey) { KeyId = keyRing.SigningKeyId }]);

        // ── Issuer side: a cookie that exists ONLY for the login page + authorize endpoint. APIs
        // never accept it (they authenticate with the default JwtBearer scheme), so the API surface
        // stays bearer-only and CSRF-immune exactly as before. ──
        authBuilder.AddCookie(LocalAuthDefaults.CookieScheme, options =>
        {
            options.Cookie.Name = ".Plenipo.Local";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // Mirrors Auth:RequireHttpsMetadata — the existing "this deployment runs plain HTTP on a
            // LAN" opt-out — instead of inventing a second knob for the same fact.
            options.Cookie.SecurePolicy = auth.RequireHttpsMetadata
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            options.LoginPath = LocalAuthDefaults.LoginPath;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
        });

        // ── The authorization server itself. ──
        services.AddHttpContextAccessor();
        services.AddOpenIddict()
            .AddCore(options => options
                .ReplaceApplicationManager<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreApplication,
                    LocalApplicationManager>())
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris(LocalAuthDefaults.AuthorizeEndpoint)
                    .SetTokenEndpointUris(LocalAuthDefaults.TokenEndpoint)
                    .SetEndSessionEndpointUris(LocalAuthDefaults.EndSessionEndpoint);

                options.AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange();

                options.RegisterScopes(Scopes.Email, Scopes.Profile, Scopes.OfflineAccess);

                options.SetAccessTokenLifetime(TimeSpan.FromHours(1))
                    .SetRefreshTokenLifetime(TimeSpan.FromDays(14));

                // Access tokens stay PLAIN signed JWTs so the stock JwtBearer handler above validates
                // them; codes and refresh tokens remain encrypted — only the server itself reads those.
                options.DisableAccessTokenEncryption();

                var aspNetCore = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();

                if (!auth.RequireHttpsMetadata)
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            });

        // Key material is appended by a DI-aware configurator (it needs the key ring singleton);
        // registered after AddServer so it runs after the lambda above, before OpenIddict validates.
        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, LocalIssuerKeyConfigurator>();

        services.AddHostedService<LocalTokenPruneService>();
    }

    /// <summary>Feeds the issuer the deployment's persisted keys, resolved lazily via the key ring.</summary>
    private sealed class LocalIssuerKeyConfigurator(ILocalAuthKeyRing keyRing) : IConfigureOptions<OpenIddictServerOptions>
    {
        public void Configure(OpenIddictServerOptions options)
        {
            options.SigningCredentials.Add(new SigningCredentials(
                new RsaSecurityKey(keyRing.SigningKey) { KeyId = keyRing.SigningKeyId },
                SecurityAlgorithms.RsaSha256));
            options.EncryptionCredentials.Add(new EncryptingCredentials(
                new SymmetricSecurityKey(keyRing.EncryptionKey),
                SecurityAlgorithms.Aes256KW,
                SecurityAlgorithms.Aes256CbcHmacSha512));
        }
    }

    /// <summary>
    /// Daily sweep of expired/consumed protocol rows (codes, refresh tokens, authorizations). OpenIddict
    /// only prunes via its Quartz integration; a periodic sweep keeps a years-running mini PC's
    /// database from accreting a token graveyard without taking on a scheduler dependency.
    /// </summary>
    private sealed class LocalTokenPruneService(
        IServiceProvider serviceProvider, ILogger<LocalTokenPruneService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // First sweep shortly after start (catches a host that only runs briefly each day),
                // then daily.
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
                do
                {
                    await PruneAsync(stoppingToken);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
                // Host shutdown.
            }
        }

        private async Task PruneAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Anything unusable for 14 days is history, not state (matches the refresh lifetime).
                var threshold = DateTimeOffset.UtcNow - TimeSpan.FromDays(14);
                await using var scope = serviceProvider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>()
                    .PruneAsync(threshold, cancellationToken);
                await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>()
                    .PruneAsync(threshold, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Local auth token pruning failed; will retry on the next sweep.");
            }
        }
    }
}

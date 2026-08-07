using Plenipo.AspNetCore.Auth.Local;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Plenipo.AspNetCore.Auth;

/// <summary>
/// Configures authentication and authorization. Three shapes (ADR 0003): external OIDC (Entra
/// External ID, Keycloak, …) via JwtBearer; <c>Auth:Mode=Local</c>, where the host is its own OpenID
/// Connect issuer; and the Development-only header fallback.
/// </summary>
public static class AuthSetup
{
    public static IServiceCollection AddPlenipoAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        if (auth.IsLocalMode && (!string.IsNullOrWhiteSpace(auth.Authority) || !string.IsNullOrWhiteSpace(auth.Audience)))
        {
            throw new InvalidOperationException(
                "Auth:Mode=Local and Auth:Authority/Audience are mutually exclusive: Local mode means the host " +
                "IS the issuer. Remove the external authority settings, or remove Auth:Mode to use them.");
        }

        if (auth.IsExplicitOidcMode && !auth.IsConfigured)
        {
            throw new InvalidOperationException(
                "Auth:Mode=Oidc requires both Auth:Authority and Auth:Audience.");
        }

        if (auth.IsPartiallyConfigured)
        {
            throw new InvalidOperationException(
                "Plenipo authentication is partially configured: both Auth:Authority and Auth:Audience are required. " +
                "Audience validation cannot be disabled for a configured JWT authority.");
        }

        var authBuilder = services.AddAuthentication(options =>
        {
            // Local mode still defaults to JwtBearer: APIs take locally minted bearer tokens through
            // the identical scheme, and the issuer's cookie is confined to its own login surface.
            var scheme = auth.IsConfigured || auth.IsLocalMode
                ? JwtBearerDefaults.AuthenticationScheme
                : DevAuthenticationHandler.SchemeName;
            options.DefaultAuthenticateScheme = scheme;
            options.DefaultChallengeScheme = scheme;
        });

        if (auth.IsLocalMode)
        {
            LocalAuthSetup.AddLocalAuth(authBuilder, services, auth);
        }
        else if (auth.IsConfigured)
        {
            authBuilder.AddJwtBearer(options =>
            {
                options.Authority = auth.Authority;
                options.Audience = auth.Audience;
                options.RequireHttpsMetadata = auth.RequireHttpsMetadata;
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    NameClaimType = "name",
                    RoleClaimType = "roles",
                };
                // Constructed unconditionally so a later handler can ATTACH an event without replacing the
                // bag — assigning options.Events inside the RequireMfa branch made this a trap where the
                // next feature to need an event would silently delete the MFA backstop SECURITY.md
                // advertises.
                options.Events = new JwtBearerEvents();
                AttachSharedJwtBearerEvents(options, auth);
            });
        }
        else if (environment.IsDevelopment())
        {
            authBuilder.AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevAuthenticationHandler>(
                DevAuthenticationHandler.SchemeName, _ => { });
        }
        else
        {
            // Outside Development with no configured auth there is NO handler for the default scheme,
            // so every request would 500. Fail fast at startup with an actionable message instead — and
            // make the contract explicit: the X-Dev-* dev-auth fallback is Development-only, never a
            // prod bypass. Built-in sign-in (Auth:Mode=Local) is an explicit choice, never a default.
            throw new InvalidOperationException(
                "Plenipo authentication is not configured: set the \"Auth\" section — an external OIDC " +
                "Authority/Audience (Entra External ID, Keycloak, …) or Auth:Mode=Local for built-in sign-in — " +
                "to run outside the Development environment. The X-Dev-* dev-auth fallback is Development-only.");
        }

        return services;
    }

    /// <summary>
    /// The JwtBearer events every bearer-validating mode shares — external OIDC and Local mode alike,
    /// so the transports and the MFA backstop cannot drift between them.
    /// </summary>
    internal static void AttachSharedJwtBearerEvents(JwtBearerOptions options, AuthOptions auth)
    {
        // A browser's WebSocket handshake cannot set an Authorization header, so SignalR sends
        // the bearer as an `access_token` query parameter instead. Accept it for HUB PATHS ONLY:
        // a query string is logged by proxies and kept in browser history, so widening this to
        // the REST surface would leak credentials into places headers never reach. The token
        // itself still goes through the identical validation — this only changes where it is
        // read from, for the one transport that cannot carry a header.
        options.Events!.OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token)
                && context.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.Ordinal))
            {
                context.Token = token;
            }

            return Task.CompletedTask;
        };

        if (auth.RequireMfa)
        {
            // MFA enrollment lives at the issuer (external IdP, or the local TOTP flow); this is the
            // platform-side backstop that a token minted WITHOUT it never authenticates, however the
            // issuer is configured.
            options.Events!.OnTokenValidated = context =>
            {
                if (context.Principal is null || !MfaEnforcement.SatisfiesMfa(context.Principal, auth))
                {
                    context.Fail("Token was not issued with multi-factor authentication (no accepted amr value).");
                }
                return Task.CompletedTask;
            };
        }
    }
}

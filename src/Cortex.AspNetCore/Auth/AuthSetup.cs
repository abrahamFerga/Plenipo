using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Cortex.AspNetCore.Auth;

/// <summary>Configures authentication (Entra External ID JWT, with a dev fallback) and authorization.</summary>
public static class AuthSetup
{
    public static IServiceCollection AddCortexAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var auth = configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        if (auth.IsPartiallyConfigured)
        {
            throw new InvalidOperationException(
                "Cortex authentication is partially configured: both Auth:Authority and Auth:Audience are required. " +
                "Audience validation cannot be disabled for a configured JWT authority.");
        }

        var authBuilder = services.AddAuthentication(options =>
        {
            var scheme = auth.IsConfigured ? JwtBearerDefaults.AuthenticationScheme : DevAuthenticationHandler.SchemeName;
            options.DefaultAuthenticateScheme = scheme;
            options.DefaultChallengeScheme = scheme;
        });

        if (auth.IsConfigured)
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
                if (auth.RequireMfa)
                {
                    // MFA enrollment lives at the IdP; this is the platform-side backstop that a
                    // token minted WITHOUT it never authenticates, however the IdP is configured.
                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = context =>
                        {
                            if (context.Principal is null || !MfaEnforcement.SatisfiesMfa(context.Principal, auth))
                            {
                                context.Fail("Token was not issued with multi-factor authentication (no accepted amr value).");
                            }
                            return Task.CompletedTask;
                        },
                    };
                }
            });
        }
        else if (environment.IsDevelopment())
        {
            authBuilder.AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevAuthenticationHandler>(
                DevAuthenticationHandler.SchemeName, _ => { });
        }
        else
        {
            // Outside Development with no Entra External ID config there is NO handler for the default scheme,
            // so every request would 500. Fail fast at startup with an actionable message instead — and make
            // the contract explicit: the X-Dev-* dev-auth fallback is Development-only, never a prod bypass.
            throw new InvalidOperationException(
                "Cortex authentication is not configured: set the \"Auth\" section (Entra External ID Authority/Audience) " +
                "to run outside the Development environment. The X-Dev-* dev-auth fallback is Development-only.");
        }

        return services;
    }
}

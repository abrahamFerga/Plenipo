using Plenipo.Infrastructure.LocalAuth;
using Plenipo.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OI = OpenIddict.Abstractions.OpenIddictConstants;

namespace Plenipo.AspNetCore.Auth.Local;

/// <summary>
/// Local-mode startup work, run by <c>DatabaseInitializer</c> after migrations and before the host
/// serves traffic: load (or first-boot-generate) the issuer's keys, and upsert the built-in public
/// client the browser shells sign in with. Upsert — not create-once — so descriptor changes shipped
/// by an upgrade converge on every boot.
/// </summary>
public static class LocalAuthInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var keyRing = services.GetRequiredService<ILocalAuthKeyRing>();
        await keyRing.EnsureInitializedAsync(services.GetRequiredService<PlatformDbContext>(), cancellationToken);

        var auth = services.GetRequiredService<IOptions<AuthOptions>>().Value;
        var configuration = services.GetRequiredService<IConfiguration>();
        var clientId = string.IsNullOrWhiteSpace(auth.ClientId) ? LocalAuthDefaults.ClientId : auth.ClientId;

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = OI.ClientTypes.Public,
            // First-party by definition — the client IS the product's own shell — so no consent page.
            ConsentType = OI.ConsentTypes.Implicit,
            DisplayName = configuration["Branding:ProductName"] ?? "Plenipo",
            Permissions =
            {
                OI.Permissions.Endpoints.Authorization,
                OI.Permissions.Endpoints.Token,
                OI.Permissions.Endpoints.EndSession,
                OI.Permissions.GrantTypes.AuthorizationCode,
                OI.Permissions.GrantTypes.RefreshToken,
                OI.Permissions.ResponseTypes.Code,
                OI.Permissions.Scopes.Email,
                OI.Permissions.Scopes.Profile,
                // offline_access needs no scope permission: OpenIddict gates it on the refresh grant.
            },
            Requirements = { OI.Requirements.Features.ProofKeyForCodeExchange },
        };

        // Same-origin callbacks are validated dynamically against the requesting host
        // (LocalApplicationManager), which no static list could enumerate for an on-prem box. These
        // registered entries exist for the DEV story only: Vite serves the shells on their own
        // origins, which the same-host rule would rightly refuse.
        descriptor.RedirectUris.Add(new Uri("http://localhost:5173/signin-callback"));
        descriptor.RedirectUris.Add(new Uri("http://localhost:5174/admin/signin-callback"));
        descriptor.PostLogoutRedirectUris.Add(new Uri("http://localhost:5173/"));
        descriptor.PostLogoutRedirectUris.Add(new Uri("http://localhost:5174/"));

        var manager = services.GetRequiredService<IOpenIddictApplicationManager>();
        if (await manager.FindByClientIdAsync(clientId, cancellationToken) is { } existing)
        {
            await manager.UpdateAsync(existing, descriptor, cancellationToken);
        }
        else
        {
            await manager.CreateAsync(descriptor, cancellationToken);
        }
    }
}

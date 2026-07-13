using Plenipo.Application.Modules;
using Plenipo.Core.Identity;

namespace Plenipo.AspNetCore.Endpoints;

/// <summary>
/// The admin console's extension surface: modules declare admin pages in their manifest
/// (<c>ModuleManifest.AdminTabs</c>), and the admin app renders them generically — the same
/// server-driven tab machinery as the domain shell, so a product adds an admin page without
/// forking <c>@plenipo/admin-ui</c>. Every admin tab is permission-gated by construction
/// (validated at startup); a caller holding none of them simply gets an empty list.
/// </summary>
public static class AdminExtensionEndpoints
{
    public static void MapAdminExtensionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/extensions", async (
                IModuleCatalog catalog, ITenantModuleStore moduleStore, ICurrentUser user,
                CancellationToken cancellationToken) =>
            {
                // A tenant that disabled a module loses its admin pages too — same rule as its
                // domain tabs, tools, and chat.
                var disabled = await moduleStore.GetDisabledModuleIdsAsync(cancellationToken);

                var extensions = catalog.Manifests
                    .Where(m => m.AdminTabs.Count > 0 && !disabled.Contains(m.Id))
                    .Select(m => new AdminExtensionDto(
                        m.Id, m.DisplayName, m.Icon, TabDtoMapper.MapTabs(m.AdminTabs, user)))
                    .Where(x => x.Tabs.Length > 0)
                    .ToArray();
                return Results.Ok(extensions);
            })
            .WithTags("Admin")
            .RequireAuthorization()
            .WithName("Admin_Extensions");
    }

    private sealed record AdminExtensionDto(string Id, string DisplayName, string? Icon, TabDto[] Tabs);
}

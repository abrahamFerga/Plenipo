using Plenipo.Application.Modules;
using Plenipo.AspNetCore.Setup;
using Plenipo.Core.Multitenancy;
using Plenipo.Infrastructure.Context;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Sdk;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.AspNetCore.Modules;

/// <summary>
/// Host-side module wiring. Each installed module is instantiated once, given a chance to register its
/// services (manifest-first), and exposed as an <see cref="IModule"/> singleton so the catalog and
/// endpoint mapping can enumerate them.
/// </summary>
public static class ModuleHostExtensions
{
    /// <summary>
    /// Configuration list of module ids a host suppresses without recompiling,
    /// e.g. <c>Modules:Exclude:0=nutrition</c>. Unlike the per-tenant runtime toggle (which hides
    /// an offered module from one tenant), exclusion removes the module from the deployment
    /// entirely — no endpoints, no tools, no catalog entry.
    /// </summary>
    public const string ExcludeKey = "Modules:Exclude";

    /// <summary>
    /// Registers a Plenipo module in the host. Call once per module in <c>Program.cs</c>.
    /// The module's services are added to the DI container and its manifest is exposed
    /// to the catalog. Modules can be shipped as NuGet packages. Honors <see cref="ExcludeKey"/>,
    /// so which modules a deployment offers is adjustable by configuration alone (ADR-0001).
    /// </summary>
    public static IHostApplicationBuilder AddPlenipoModule<TModule>(this IHostApplicationBuilder builder)
        where TModule : class, IModule, new()
    {
        var module = new TModule();
        var excluded = builder.Configuration.GetSection(ExcludeKey).GetChildren()
            .Any(c => string.Equals(c.Value, module.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (excluded)
        {
            return builder;
        }

        module.RegisterServices(builder.Services, builder.Configuration);
        builder.Services.AddSingleton<IModule>(module);
        return builder;
    }

    public static void MapPlenipoModules(this IEndpointRouteBuilder app)
    {
        // Build the module catalog now so an invalid registration (duplicate ids, colliding tab routes)
        // fails fast at startup with a clear message, instead of on the first request that resolves it.
        app.ServiceProvider.GetRequiredService<IModuleCatalog>();

        foreach (var module in app.ServiceProvider.GetServices<IModule>())
        {
            // Wrap each module's endpoints in a group whose filter 404s every route when the module is
            // disabled for the caller's tenant — a tenant-scoped kill switch, consistent with the workspace
            // catalog (which hides it) and the agent runner (which refuses chat to it).
            var moduleEndpoints = app.MapGroup("").AddEndpointFilter(new ModuleEnabledFilter(module.Manifest.Id));
            module.MapEndpoints(moduleEndpoints);
        }
    }

    public static async Task MigratePlenipoModulesAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        using var scope = host.Services.CreateScope();
        foreach (var module in scope.ServiceProvider.GetServices<IModule>())
        {
            await module.MigrateAsync(scope.ServiceProvider, cancellationToken);
        }
    }

    public static async Task SeedPlenipoModulesAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;

        // In Development, run module seeding inside the dev tenant's context so a module can seed
        // tenant-owned demo data against ITenantContext exactly as its request handlers do. In other
        // environments there is no ambient tenant here, so tenant-scoped demo seeds correctly no-op.
        await EstablishDevTenantContextAsync(services, cancellationToken);

        foreach (var module in services.GetServices<IModule>())
        {
            await module.SeedAsync(services, cancellationToken);
        }
    }

    private static async Task EstablishDevTenantContextAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var environment = services.GetService<IHostEnvironment>();
        if (environment is null || !environment.IsDevelopment())
        {
            return;
        }

        // RequestContext is the mutable ITenantContext; without it (or the dev tenant) there is nothing to set.
        if (services.GetService<RequestContext>() is not { } context)
        {
            return;
        }

        var platform = services.GetRequiredService<PlatformDbContext>();
        var devTenant = await platform.Tenants
            .FirstOrDefaultAsync(t => t.Slug == DatabaseInitializer.DevTenantSlug, cancellationToken);
        if (devTenant is not null)
        {
            context.SetTenant(devTenant.Id);
        }
    }
}

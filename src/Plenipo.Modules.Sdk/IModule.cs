using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Modules.Sdk;

/// <summary>
/// The contract every domain module implements. Discovered at startup, a module declares its
/// capabilities through <see cref="Manifest"/> (manifest-first), wires its own services, and maps its
/// own HTTP endpoints. This is the single seam through which an industry vertical — legal, finance,
/// medical — plugs into the platform.
/// </summary>
public interface IModule
{
    /// <summary>Static capability declaration. Read before any other member is called.</summary>
    public ModuleManifest Manifest { get; }

    /// <summary>Register the module's services, tool source, and options into the host container.</summary>
    public void RegisterServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Map the module's API endpoints. Implementations should require module authorization.</summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints);

    /// <summary>
    /// Optional: migrate the module's own database(s). Called by the host at startup, after the
    /// platform databases are migrated. Modules that own persistence apply their DbContext migrations
    /// here. No-op by default.
    /// </summary>
    public Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>Optional one-time seed (reference data, defaults). No-op by default.</summary>
    public Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

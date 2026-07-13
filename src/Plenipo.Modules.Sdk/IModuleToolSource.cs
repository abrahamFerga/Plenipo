using Microsoft.Extensions.AI;

namespace Plenipo.Modules.Sdk;

/// <summary>
/// Supplies a module's executable tools. Invoked inside the active request scope so the produced
/// <see cref="AIFunction"/>s may close over scoped services (DbContext, current user, etc.).
/// A module registers exactly one source for itself.
/// </summary>
public interface IModuleToolSource
{
    /// <summary>The module these tools belong to. Must match the module's manifest id.</summary>
    public string ModuleId { get; }

    /// <summary>Build the module's tools using services resolved from the current scope.</summary>
    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices);
}

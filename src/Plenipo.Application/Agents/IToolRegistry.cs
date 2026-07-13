using Plenipo.Modules.Sdk;

namespace Plenipo.Application.Agents;

/// <summary>
/// Aggregates the executable tools contributed by modules. Resolves them within the current request
/// scope so tool functions can use scoped services. The agent runner then filters by permission.
/// </summary>
public interface IToolRegistry
{
    /// <summary>All tools for a module, built from the given scope. Empty if the module exposes none.</summary>
    public IReadOnlyList<ModuleTool> GetModuleTools(string moduleId, IServiceProvider scopedServices);
}

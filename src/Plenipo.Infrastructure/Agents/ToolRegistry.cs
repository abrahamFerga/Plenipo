using Plenipo.Application.Agents;
using Plenipo.Modules.Sdk;

namespace Plenipo.Infrastructure.Agents;

/// <summary>
/// Aggregates module-contributed tool sources and resolves a module's tools within the caller's scope,
/// so the produced functions can use scoped services (DbContext, current user, …). Platform-wide tools
/// (<see cref="IPlatformToolSource"/> — documents, files) are appended to every module's set; each
/// remains individually permission-gated by the runner before the model sees any schema.
/// </summary>
public sealed class ToolRegistry(
    IEnumerable<IModuleToolSource> sources,
    IEnumerable<IPlatformToolSource> platformSources) : IToolRegistry
{
    public IReadOnlyList<ModuleTool> GetModuleTools(string moduleId, IServiceProvider scopedServices)
    {
        var tools = new List<ModuleTool>();

        foreach (var source in sources)
        {
            if (string.Equals(source.ModuleId, moduleId, StringComparison.Ordinal))
            {
                tools.AddRange(source.GetTools(scopedServices));
                break;
            }
        }

        foreach (var platformSource in platformSources)
        {
            tools.AddRange(platformSource.GetTools(scopedServices));
        }

        return tools;
    }
}

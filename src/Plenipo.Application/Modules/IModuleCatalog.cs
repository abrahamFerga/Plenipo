using Plenipo.Modules.Sdk;

namespace Plenipo.Application.Modules;

/// <summary>
/// The set of modules loaded into this host. Built once at startup from discovered <see cref="IModule"/>
/// implementations. Drives the dashboard's navigation, the agent's per-module instructions, and the
/// platform modules endpoint.
/// </summary>
public interface IModuleCatalog
{
    public IReadOnlyList<ModuleManifest> Manifests { get; }

    public bool TryGetManifest(string moduleId, out ModuleManifest? manifest);
}

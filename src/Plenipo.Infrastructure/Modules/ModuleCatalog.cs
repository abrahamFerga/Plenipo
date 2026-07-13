using Plenipo.Application.Modules;
using Plenipo.Modules.Sdk;

namespace Plenipo.Infrastructure.Modules;

/// <summary>The loaded modules, indexed by id. Built once at startup from the discovered modules.</summary>
public sealed class ModuleCatalog : IModuleCatalog
{
    private readonly Dictionary<string, ModuleManifest> _byId;

    public ModuleCatalog(IEnumerable<IModule> modules)
    {
        var manifests = modules.Select(m => m.Manifest).ToList();
        // Fail fast with an actionable message on a bad registration (duplicate ids, colliding routes)
        // rather than letting ToDictionary throw a cryptic "same key" ArgumentException below.
        ModuleManifestValidator.ThrowIfInvalid(manifests);
        _byId = manifests.ToDictionary(m => m.Id, m => m, StringComparer.Ordinal);
        Manifests = [.. _byId.Values];
    }

    public IReadOnlyList<ModuleManifest> Manifests { get; }

    public bool TryGetManifest(string moduleId, out ModuleManifest? manifest) =>
        _byId.TryGetValue(moduleId, out manifest);
}

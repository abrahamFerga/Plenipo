using Plenipo.Application.Ai;
using Plenipo.Application.Modules;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Sdk;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Ai;

/// <summary>
/// Merges the two agent sources by name, tenant profile winning: tenant-created
/// <see cref="AgentProfile"/> rows (tenant scoping via the global query filter) and the module
/// manifest's code-first <see cref="AgentDescriptor"/>s (mapped to unpersisted profiles so the
/// runner consumes one shape). No row and no manifest default means "no customization" — the
/// manifest instructions apply as-is.
/// </summary>
public sealed class AgentProfileResolver(PlatformDbContext db, IModuleCatalog modules) : IAgentProfileResolver
{
    public async Task<AgentProfile?> ResolveActiveAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var row = await db.AgentProfiles.FirstOrDefaultAsync(
            p => p.ModuleId == moduleId && p.IsDefault, cancellationToken);
        if (row is not null)
        {
            return row;
        }

        var manifestDefault = ManifestAgents(moduleId).FirstOrDefault(a => a.IsDefault);
        return manifestDefault is null ? null : FromDescriptor(moduleId, manifestDefault);
    }

    public async Task<AgentProfile?> ResolveNamedAsync(
        string moduleId, string agentName, CancellationToken cancellationToken = default)
    {
        var row = await db.AgentProfiles.FirstOrDefaultAsync(
            p => p.ModuleId == moduleId && p.Name == agentName, cancellationToken);
        if (row is not null)
        {
            return row;
        }

        var descriptor = ManifestAgents(moduleId)
            .FirstOrDefault(a => string.Equals(a.Name, agentName, StringComparison.Ordinal));
        return descriptor is null ? null : FromDescriptor(moduleId, descriptor);
    }

    private IEnumerable<AgentDescriptor> ManifestAgents(string moduleId) =>
        modules.TryGetManifest(moduleId, out var manifest) && manifest is not null
            ? manifest.Agents
            : [];

    /// <summary>Never persisted — a manifest agent worn as the profile shape the runner consumes.</summary>
    private static AgentProfile FromDescriptor(string moduleId, AgentDescriptor d) => new()
    {
        ModuleId = moduleId,
        Name = d.Name,
        Instructions = d.Instructions,
        Mode = d.ReplaceInstructions ? AgentProfileMode.Replace : AgentProfileMode.Append,
        ToolNames = d.ToolNames?.ToList(),
        Model = d.Model,
        IsDefault = d.IsDefault,
    };
}

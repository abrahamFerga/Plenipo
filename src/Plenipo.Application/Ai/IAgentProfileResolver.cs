using Plenipo.Core.Platform;

namespace Plenipo.Application.Ai;

/// <summary>
/// Resolves which agent applies to a chat turn. Agents come from two places — tenant-created
/// profiles (DB, admin-managed) and the module manifest's code-first <c>Agents</c> — merged by
/// name with the tenant profile winning, so an admin can override a module-shipped agent without
/// touching code.
/// </summary>
public interface IAgentProfileResolver
{
    /// <summary>
    /// The default agent when the user picked none: the tenant's default profile, else the
    /// manifest agent marked <c>IsDefault</c>, else <c>null</c> (the plain module assistant).
    /// </summary>
    public Task<AgentProfile?> ResolveActiveAsync(string moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The agent the user explicitly picked, by name — a tenant profile first, else a manifest
    /// agent. <c>null</c> means the name is unknown for this module (fail the turn readably;
    /// never silently fall back to a different agent than the one asked for).
    /// </summary>
    public Task<AgentProfile?> ResolveNamedAsync(string moduleId, string agentName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Composes the effective agent instructions for a turn: tenant system prompt, then the module
/// manifest's instructions, then the active profile per its mode. Pure — the merge semantics are
/// the contract admins rely on when they pick Append vs Replace, so they are unit-tested.
/// </summary>
public static class InstructionComposer
{
    public static string Compose(string systemPrompt, string? manifestInstructions, AgentProfile? profile)
    {
        var parts = new List<string>(3) { systemPrompt };

        if (profile is { Mode: AgentProfileMode.Replace })
        {
            parts.Add(profile.Instructions);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(manifestInstructions))
            {
                parts.Add(manifestInstructions);
            }

            if (profile is not null)
            {
                parts.Add(profile.Instructions);
            }
        }

        return string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}

using Microsoft.Extensions.AI;

namespace Plenipo.Application.Ai;

/// <summary>
/// Resolves the <see cref="IChatClient"/> a turn should run on: the tenant's effective provider
/// connection (deployment default, or the tenant's own provider + vaulted key), with an optional
/// per-agent model override (from the agent profile). Returns null when the effective provider is
/// "None"; throws <see cref="InvalidOperationException"/> with a human-readable message when the
/// connection is misconfigured (e.g. a key that no longer reveals) — the runner surfaces it as a
/// failed turn, never a 500.
/// </summary>
public interface ITenantChatClientResolver
{
    public Task<IChatClient?> ResolveAsync(EffectiveAiSettings settings, string? modelOverride, CancellationToken cancellationToken = default);
}

using System.Collections.Concurrent;
using Cortex.Application.Ai;
using Cortex.Application.Secrets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Cortex.Infrastructure.Ai;

/// <summary>
/// Builds (and caches) the <see cref="IChatClient"/> for a turn's effective provider connection.
/// The cache key is the full connection identity — provider, model, endpoint, and the vault
/// REFERENCE of the tenant-vaulted key — so switching provider/model/key in the admin UI takes effect on the
/// very next turn (a new key means a new reference means a new cache entry), while steady-state
/// turns reuse one client per distinct connection.
/// </summary>
public sealed class TenantChatClientResolver(
    IOptions<AiOptions> aiOptions,
    ISecretVault vault) : ITenantChatClientResolver
{
    /// <summary>Vault scope for tenant-entered AI API keys (write-only through the admin API).</summary>
    public const string ApiKeyScope = "Cortex.Ai.ApiKey";

    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();

    public async Task<IChatClient?> ResolveAsync(
        EffectiveAiSettings settings, string? modelOverride, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsEnabled)
        {
            return null;
        }

        var model = string.IsNullOrWhiteSpace(modelOverride) ? settings.Model : modelOverride;
        var cacheKey = $"{settings.Provider}|{model}|{settings.Endpoint}|{settings.ApiKeySecretRef ?? "(keyless)"}";
        if (_clients.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var defaults = aiOptions.Value;
        var apiKey = settings.ApiKeySecretRef is { } secretRef
            ? await vault.RevealAsync(ApiKeyScope, secretRef, cancellationToken)
            : null;
        var options = new AiOptions
        {
            Provider = settings.Provider,
            Model = model,
            Endpoint = settings.Endpoint,
            Temperature = defaults.Temperature,
            MaxOutputTokens = defaults.MaxOutputTokens,
        };

        // GetOrAdd would race the async reveal above; a duplicate build is harmless and transient.
        return _clients.GetOrAdd(cacheKey, _ => ChatClientFactory.Create(options, apiKey));
    }
}

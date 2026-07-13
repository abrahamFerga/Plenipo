using Plenipo.Core.Platform;

namespace Plenipo.Application.Ai;

/// <summary>The effective AI settings for the current tenant — deployment defaults with per-tenant overrides applied.</summary>
public sealed record EffectiveAiSettings(string SystemPrompt, int MaxConversationTokens, long MaxMonthlyTokens)
{
    /// <summary>The provider chat runs on for this tenant (Mock | OpenAI | AzureOpenAI | Ollama | None).</summary>
    public string Provider { get; init; } = "None";

    /// <summary>The default model/deployment name (an agent profile may pin its own per agent).</summary>
    public string Model { get; init; } = "";

    public string? Endpoint { get; init; }

    /// <summary>Vault reference to the tenant's own API key; null = a keyless/managed-identity connection.</summary>
    public string? ApiKeySecretRef { get; init; }

    /// <summary>True when this tenant overrode the provider connection (its own provider/endpoint/key).</summary>
    public bool UsesTenantProvider { get; init; }

    /// <summary>Models the chat's model picker offers (see <see cref="AiOptions.AvailableModels"/>).</summary>
    public IReadOnlyList<string> AvailableModels { get; init; } = [];

    public bool IsEnabled => !string.Equals(Provider, "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>A per-turn model override is valid when it IS the default or is on the advertised list.</summary>
    public bool AllowsModel(string model) =>
        string.Equals(model, Model, StringComparison.Ordinal) ||
        AvailableModels.Contains(model, StringComparer.Ordinal);

    /// <summary>
    /// Layer a tenant's overrides over the deployment defaults: a null or whitespace-only system prompt, and a
    /// null token budget, inherit the default — so an untouched (or blanked) override is transparent. A tenant
    /// token budget of <c>0</c> is honoured as "unlimited" (matching <see cref="AiOptions.MaxConversationTokens"/>),
    /// distinct from <c>null</c> which inherits. The same null/0 semantics apply to the monthly budget.
    /// Provider semantics: a null <c>row.Provider</c> inherits the deployment's whole connection (a bare
    /// <c>row.Model</c> may still pick a different model on it); a set provider brings the tenant's OWN
    /// connection — its endpoint and vaulted key — and never mixes with the deployment's endpoint/key.
    /// </summary>
    public static EffectiveAiSettings Merge(TenantAiSettings? row, AiOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        var usesTenantProvider = !string.IsNullOrWhiteSpace(row?.Provider);
        return new(
            string.IsNullOrWhiteSpace(row?.SystemPrompt) ? defaults.SystemPrompt : row!.SystemPrompt!,
            row?.MaxConversationTokens ?? defaults.MaxConversationTokens,
            row?.MaxMonthlyTokens ?? defaults.MaxMonthlyTokens)
        {
            Provider = usesTenantProvider ? row!.Provider! : defaults.Provider,
            Model = (string.IsNullOrWhiteSpace(row?.Model) ? null : row!.Model) ?? defaults.Model,
            Endpoint = usesTenantProvider ? row!.Endpoint : defaults.Endpoint,
            ApiKeySecretRef = usesTenantProvider ? row!.ApiKeySecretRef : null,
            UsesTenantProvider = usesTenantProvider,
            AvailableModels = defaults.AvailableModels,
        };
    }

    /// <summary>Prompt/budget-only overload (no tenant row entity at hand); provider fields inherit the defaults.</summary>
    public static EffectiveAiSettings Merge(
        string? overrideSystemPrompt, int? overrideMaxConversationTokens, long? overrideMaxMonthlyTokens, AiOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return Merge(new TenantAiSettings
        {
            SystemPrompt = overrideSystemPrompt,
            MaxConversationTokens = overrideMaxConversationTokens,
            MaxMonthlyTokens = overrideMaxMonthlyTokens,
        }, defaults);
    }
}

/// <summary>
/// Resolves the AI settings that apply to the current tenant: the <c>Ai</c> configuration defaults, with any
/// per-tenant overrides layered on top. The agent runner consults this each turn, so a tenant's custom
/// system prompt, token budget, or provider connection takes effect without touching the global config.
/// </summary>
public interface ITenantAiSettings
{
    public Task<EffectiveAiSettings> ResolveAsync(CancellationToken cancellationToken = default);
}

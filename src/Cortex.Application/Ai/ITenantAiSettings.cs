namespace Cortex.Application.Ai;

/// <summary>The effective AI settings for the current tenant — deployment defaults with per-tenant overrides applied.</summary>
public sealed record EffectiveAiSettings(string SystemPrompt, int MaxConversationTokens, long MaxMonthlyTokens)
{
    /// <summary>
    /// Layer a tenant's overrides over the deployment defaults: a null or whitespace-only system prompt, and a
    /// null token budget, inherit the default — so an untouched (or blanked) override is transparent. A tenant
    /// token budget of <c>0</c> is honoured as "unlimited" (matching <see cref="AiOptions.MaxConversationTokens"/>),
    /// distinct from <c>null</c> which inherits. The same null/0 semantics apply to the monthly budget.
    /// </summary>
    public static EffectiveAiSettings Merge(
        string? overrideSystemPrompt, int? overrideMaxConversationTokens, long? overrideMaxMonthlyTokens, AiOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return new(
            string.IsNullOrWhiteSpace(overrideSystemPrompt) ? defaults.SystemPrompt : overrideSystemPrompt,
            overrideMaxConversationTokens ?? defaults.MaxConversationTokens,
            overrideMaxMonthlyTokens ?? defaults.MaxMonthlyTokens);
    }
}

/// <summary>
/// Resolves the AI settings that apply to the current tenant: the <c>Ai</c> configuration defaults, with any
/// per-tenant overrides layered on top. The agent runner consults this each turn, so a tenant's custom
/// system prompt or token budget takes effect without touching the global config.
/// </summary>
public interface ITenantAiSettings
{
    public Task<EffectiveAiSettings> ResolveAsync(CancellationToken cancellationToken = default);
}

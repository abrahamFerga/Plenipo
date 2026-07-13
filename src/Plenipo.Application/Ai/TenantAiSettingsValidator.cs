namespace Plenipo.Application.Ai;

/// <summary>
/// Validates a tenant's AI-settings override before it is persisted. Both fields are supplied by a tenant
/// admin, so these are guardrails against accidents — a negative budget that would silently disable the cap
/// (<see cref="TenantAiSettings"/>'s budget is enforced only when positive), or a system prompt long enough to
/// exceed the storage column (which would otherwise surface as a 500 at save time and inflate every turn).
/// </summary>
public static class TenantAiSettingsValidator
{
    /// <summary>
    /// The maximum length of an override system prompt. This is the single source of truth for the bound — the
    /// <c>tenant_ai_settings.SystemPrompt</c> column is configured with the same value, so a prompt that passes
    /// this check always fits the column (a longer one gets a clean 400 instead of a 500 <c>DbUpdateException</c>).
    /// </summary>
    public const int MaxSystemPromptLength = 8000;

    /// <summary>Returns a human-readable error when the override is invalid, or <c>null</c> when it is acceptable.</summary>
    public static string? Validate(string? systemPrompt, int? maxConversationTokens, long? maxMonthlyTokens = null)
    {
        if (maxConversationTokens is < 0)
        {
            return "maxConversationTokens must be zero or greater.";
        }

        if (maxMonthlyTokens is < 0)
        {
            return "maxMonthlyTokens must be zero or greater.";
        }

        if (systemPrompt is { Length: > MaxSystemPromptLength })
        {
            return $"systemPrompt must be {MaxSystemPromptLength:N0} characters or fewer.";
        }

        return null;
    }

    /// <summary>Providers a tenant may switch to at runtime ("None" disables chat for the tenant) — the shared list.</summary>
    public static readonly IReadOnlyList<string> AllowedProviders = AiProviders.All;

    /// <summary>
    /// Validates a tenant's provider-connection override. <paramref name="hasApiKey"/> is whether a
    /// key WILL be on file after this save (a newly supplied one, or an existing vaulted one being
    /// kept) — the key itself never reaches validation.
    /// </summary>
    public static string? ValidateProvider(string? provider, string? model, string? endpoint, bool hasApiKey)
    {
        if (model is { Length: > 200 })
        {
            return "model must be 200 characters or fewer.";
        }

        if (endpoint is not null &&
            (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            return "endpoint must be an absolute http(s) URL.";
        }

        if (provider is null)
        {
            return null; // inheriting the deployment connection; a bare model override is fine
        }

        if (!AllowedProviders.Contains(provider, StringComparer.Ordinal))
        {
            return $"provider must be one of: {string.Join(", ", AllowedProviders)} — or empty to use the deployment default.";
        }

        return provider switch
        {
            "OpenAI" when string.IsNullOrWhiteSpace(model) => "model is required for the OpenAI provider.",
            "OpenAI" when !hasApiKey => "An API key is required for the OpenAI provider.",
            "Anthropic" when string.IsNullOrWhiteSpace(model) => "model is required for the Anthropic provider.",
            "Anthropic" when !hasApiKey => "An API key is required for the Anthropic provider.",
            "AzureOpenAI" when string.IsNullOrWhiteSpace(model) => "model (deployment name) is required for the AzureOpenAI provider.",
            "AzureOpenAI" when string.IsNullOrWhiteSpace(endpoint) => "endpoint is required for the AzureOpenAI provider.",
            "Ollama" when string.IsNullOrWhiteSpace(model) => "model is required for the Ollama provider.",
            "Ollama" when string.IsNullOrWhiteSpace(endpoint) => "endpoint is required for the Ollama provider (e.g. http://localhost:11434/v1).",
            _ => null,
        };
    }
}

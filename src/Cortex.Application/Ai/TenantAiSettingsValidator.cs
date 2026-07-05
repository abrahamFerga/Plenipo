namespace Cortex.Application.Ai;

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
}

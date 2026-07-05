namespace Cortex.Application.Ai;

/// <summary>
/// Provider-swappable LLM configuration, bound from the "Ai" configuration section. Lets a deployment
/// move between OpenAI, Azure OpenAI, and a local Ollama (via its OpenAI-compatible endpoint) without
/// code changes. Secrets come from Key Vault / user-secrets, never source.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>
    /// One of: OpenAI, AzureOpenAI, Ollama, Mock, None. "None" disables the agent surface;
    /// "Mock" enables a dependency-free canned assistant for local dev/demos (no API key needed).
    /// </summary>
    public string Provider { get; set; } = "None";

    /// <summary>Model id (OpenAI) or deployment name (Azure OpenAI).</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    public string? ApiKey { get; set; }

    /// <summary>Azure OpenAI endpoint, or the Ollama OpenAI-compatible base URL (e.g. http://localhost:11434/v1).</summary>
    public string? Endpoint { get; set; }

    public float Temperature { get; set; }

    public int MaxOutputTokens { get; set; } = 4096;

    /// <summary>
    /// Maximum total tokens a single conversation may consume before further turns are refused.
    /// 0 (the default) means unlimited. Enforced by the agent runner against recorded usage.
    /// </summary>
    public int MaxConversationTokens { get; set; }

    /// <summary>
    /// Tenant-wide token cap per calendar month (UTC); 0 (the default) means unlimited. Chat is
    /// refused once reached; tenant admins are notified at 80% and at exhaustion.
    /// </summary>
    public long MaxMonthlyTokens { get; set; }

    /// <summary>Upper bound on agent tool-calling iterations per turn.</summary>
    public int MaxToolIterations { get; set; } = 8;

    /// <summary>Base system prompt; module-specific instructions are appended per conversation.</summary>
    public string SystemPrompt { get; set; } = "You are Cortex, a helpful and precise AI assistant.";

    public bool IsEnabled => !string.Equals(Provider, "None", StringComparison.OrdinalIgnoreCase);
}

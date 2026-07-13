namespace Plenipo.Application.Ai;

/// <summary>
/// THE provider list — the single source of truth the validators (and error messages) reference,
/// so adding a provider is one edit here plus its <c>ChatClientFactory</c> case, instead of a
/// scavenger hunt across validators, factories, and copy.
/// </summary>
public static class AiProviders
{
    /// <summary>Disables the agent surface entirely.</summary>
    public const string None = "None";

    /// <summary>The dependency-free canned assistant for local dev/demos (no API key needed).</summary>
    public const string Mock = "Mock";

    public const string OpenAI = "OpenAI";
    public const string AzureOpenAI = "AzureOpenAI";
    public const string Anthropic = "Anthropic";
    public const string Ollama = "Ollama";

    /// <summary>Every valid value of <c>Ai:Provider</c> (deployment default and tenant override alike).</summary>
    public static readonly IReadOnlyList<string> All = [None, Mock, OpenAI, AzureOpenAI, Anthropic, Ollama];

    /// <summary>For messages: "None, Mock, OpenAI, AzureOpenAI, Anthropic, Ollama".</summary>
    public static readonly string AllList = string.Join(", ", All);
}

namespace Plenipo.Application.Ai;

/// <summary>
/// Validates <see cref="AiOptions"/> so a misconfigured provider fails fast at startup with an actionable
/// message — instead of throwing on the first chat turn (the <c>IChatClient</c> is built lazily). Encodes
/// what each deployment-default provider actually needs. OpenAI and Anthropic credentials are deliberately
/// tenant-vault-only and therefore cannot be deployment defaults. Pure and side-effect free.
/// </summary>
public static class AiOptionsValidator
{
    // The provider universe lives in ONE place — see AiProviders.

    /// <summary>Returns every problem found in <paramref name="options"/> (empty when valid).</summary>
    public static IReadOnlyList<string> Validate(AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        var provider = options.Provider;
        if (string.IsNullOrWhiteSpace(provider))
        {
            errors.Add($"Ai:Provider is required. Valid values: {AiProviders.AllList}.");
        }
        else if (!AiProviders.All.Contains(provider, StringComparer.Ordinal))
        {
            errors.Add($"Unknown Ai:Provider '{provider}'. Valid values: {AiProviders.AllList} (case-sensitive).");
        }

        // What each credentialed provider requires to build a working client (see ChatClientFactory).
        switch (provider)
        {
            case "OpenAI":
                RequireModel(options, errors);
                errors.Add("OpenAI must be configured per tenant in Admin → AI Settings; deployment API keys are not supported.");
                break;
            case "AzureOpenAI":
                RequireModel(options, errors);
                RequireAbsoluteEndpoint(options, "AzureOpenAI", errors);
                break;
            case "Anthropic":
                RequireModel(options, errors);
                errors.Add("Anthropic must be configured per tenant in Admin → AI Settings; deployment API keys are not supported.");
                break;
            case "Ollama":
                RequireModel(options, errors);
                RequireAbsoluteEndpoint(options, "Ollama", errors);
                break;
        }

        // Numeric sanity — applies to any provider; every default value passes.
        if (options.MaxToolIterations < 1)
        {
            errors.Add($"Ai:MaxToolIterations must be at least 1 (was {options.MaxToolIterations}).");
        }
        if (options.MaxOutputTokens < 1)
        {
            errors.Add($"Ai:MaxOutputTokens must be at least 1 (was {options.MaxOutputTokens}).");
        }
        if (options.MaxConversationTokens < 0)
        {
            errors.Add($"Ai:MaxConversationTokens cannot be negative (was {options.MaxConversationTokens}); use 0 for unlimited.");
        }
        if (options.Temperature < 0)
        {
            errors.Add($"Ai:Temperature cannot be negative (was {options.Temperature}).");
        }

        return errors;
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> with all problems aggregated if invalid.</summary>
    public static void ThrowIfInvalid(AiOptions options)
    {
        var errors = Validate(options);
        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Invalid Plenipo AI configuration (the \"Ai\" section):" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(e => "  • " + e)));
    }

    private static void RequireModel(AiOptions options, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            errors.Add($"Ai:Model is required for the {options.Provider} provider.");
        }
    }

    private static void RequireAbsoluteEndpoint(AiOptions options, string provider, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            errors.Add($"Ai:Endpoint is required for the {provider} provider.");
        }
        else if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _))
        {
            errors.Add($"Ai:Endpoint must be an absolute URL for the {provider} provider (was '{options.Endpoint}').");
        }
    }
}

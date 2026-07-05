using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Cortex.Application.Ai;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Cortex.Infrastructure.Ai;

/// <summary>
/// Builds a provider-agnostic <see cref="IChatClient"/> from <see cref="AiOptions"/>. Swapping between
/// OpenAI, Azure OpenAI, and a local Ollama (through its OpenAI-compatible endpoint) is a configuration
/// change, never a code change. The returned client is the raw provider client; the agent layer wraps
/// it with tool dispatch per request.
/// </summary>
public static class ChatClientFactory
{
    public static IChatClient Create(AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Provider switch
        {
            "OpenAI" => CreateOpenAI(options),
            "AzureOpenAI" => CreateAzureOpenAI(options),
            "Anthropic" => CreateAnthropic(options),
            "Ollama" => CreateOllama(options),
            "Mock" => new MockChatClient(),
            _ => throw new InvalidOperationException(
                $"AI provider '{options.Provider}' is not supported. Use OpenAI, AzureOpenAI, Anthropic, Ollama, or Mock."),
        };
    }

    private static IChatClient CreateAnthropic(AiOptions options)
    {
        var apiKey = Require(options.ApiKey, "Ai:ApiKey is required for the Anthropic provider.");
        // The Anthropic client selects the model per request (ChatOptions.ModelId), so pin the
        // configured model on the client — matching the per-connection caching upstream.
        return new ChatClientBuilder((IChatClient)new Anthropic.SDK.AnthropicClient(new Anthropic.SDK.APIAuthentication(apiKey)).Messages)
            .ConfigureOptions(o => o.ModelId ??= options.Model)
            .Build();
    }

    private static IChatClient CreateOpenAI(AiOptions options)
    {
        var apiKey = Require(options.ApiKey, "Ai:ApiKey is required for the OpenAI provider.");
        return new OpenAIClient(new ApiKeyCredential(apiKey))
            .GetChatClient(options.Model)
            .AsIChatClient();
    }

    private static IChatClient CreateAzureOpenAI(AiOptions options)
    {
        var endpoint = new Uri(Require(options.Endpoint, "Ai:Endpoint is required for the AzureOpenAI provider."));

        // Prefer a key when supplied; otherwise use managed identity / developer credentials.
        var client = string.IsNullOrWhiteSpace(options.ApiKey)
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
            : new AzureOpenAIClient(endpoint, new ApiKeyCredential(options.ApiKey));

        return client.GetChatClient(options.Model).AsIChatClient();
    }

    private static IChatClient CreateOllama(AiOptions options)
    {
        // Ollama exposes an OpenAI-compatible API; any non-empty key satisfies the client.
        var endpoint = new Uri(Require(options.Endpoint, "Ai:Endpoint is required for the Ollama provider (e.g. http://localhost:11434/v1)."));
        return new OpenAIClient(new ApiKeyCredential("ollama"), new OpenAIClientOptions { Endpoint = endpoint })
            .GetChatClient(options.Model)
            .AsIChatClient();
    }

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
}

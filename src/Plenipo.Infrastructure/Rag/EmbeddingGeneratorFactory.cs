using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using Plenipo.Application.Ai;
using Plenipo.Application.Rag;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// Builds the <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> from <see cref="RagOptions"/>,
/// mirroring <see cref="Ai.ChatClientFactory"/>: provider swap is configuration, never code. The
/// Embedding credentials are configured independently under <c>Rag</c>; chat credentials are
/// tenant-vault-only and are never borrowed by the deployment RAG pipeline.
/// </summary>
public static class EmbeddingGeneratorFactory
{
    public static IEmbeddingGenerator<string, Embedding<float>> Create(RagOptions options, AiOptions ai)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ai);

        return options.EmbeddingProvider switch
        {
            "Mock" => new MockEmbeddingGenerator(),
            "OpenAI" => CreateOpenAI(options, ai),
            "AzureOpenAI" => CreateAzureOpenAI(options, ai),
            "Ollama" => CreateOllama(options),
            _ => throw new InvalidOperationException(
                $"Embedding provider '{options.EmbeddingProvider}' is not supported. Use Mock, OpenAI, AzureOpenAI, or Ollama."),
        };
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateOpenAI(RagOptions options, AiOptions ai)
    {
        var apiKey = Require(options.ApiKey, "Rag:ApiKey is required for the OpenAI embedding provider.");
        return new OpenAIClient(new ApiKeyCredential(apiKey))
            .GetEmbeddingClient(options.EmbeddingModel)
            .AsIEmbeddingGenerator();
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateAzureOpenAI(RagOptions options, AiOptions ai)
    {
        var endpoint = new Uri(Require(options.Endpoint ?? ai.Endpoint, "Rag:Endpoint (or Ai:Endpoint) is required for the AzureOpenAI embedding provider."));
        var apiKey = options.ApiKey;

        var client = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
            : new AzureOpenAIClient(endpoint, new ApiKeyCredential(apiKey));

        return client.GetEmbeddingClient(options.EmbeddingModel).AsIEmbeddingGenerator();
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateOllama(RagOptions options)
    {
        var endpoint = new Uri(Require(options.Endpoint, "Rag:Endpoint is required for the Ollama embedding provider (e.g. http://localhost:11434/v1)."));
        return new OpenAIClient(new ApiKeyCredential("ollama"), new OpenAIClientOptions { Endpoint = endpoint })
            .GetEmbeddingClient(options.EmbeddingModel)
            .AsIEmbeddingGenerator();
    }

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(message) : value;
}

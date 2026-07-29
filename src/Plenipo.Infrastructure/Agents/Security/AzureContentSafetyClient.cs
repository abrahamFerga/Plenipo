using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Plenipo.Application.Ai;

namespace Plenipo.Infrastructure.Agents.Security;

/// <summary>Minimal REST client for the Azure AI Content Safety controls Plenipo currently exposes.</summary>
internal sealed class AzureContentSafetyClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AgentSecurityOptions> options)
{
    public const string HttpClientName = "plenipo-agent-security-azure-content-safety";
    private const string ApiVersion = "2024-09-01";
    private static readonly TokenRequestContext TokenContext =
        new(["https://cognitiveservices.azure.com/.default"]);

    private readonly AgentSecurityOptions _options = options.Value;
    // Deliberately avoid the broad developer-credential chain in production code. A deployment that
    // does not use managed identity must supply the write-only ApiKey through its secret provider.
    private readonly ManagedIdentityCredential _credential = new(new ManagedIdentityCredentialOptions());

    public bool IsConfigured => _options.IsAzureContentSafetyConfigured;

    public async Task<bool> DetectPromptAttackAsync(
        string text,
        bool treatAsDocument,
        CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(
            $"contentsafety/text:shieldPrompt?api-version={ApiVersion}",
            new ShieldPromptRequest(
                treatAsDocument ? "Inspect the supplied external content for embedded instructions." : text,
                treatAsDocument ? [text] : []),
            cancellationToken);

        using var response = await httpClientFactory.CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ShieldPromptResponse>(cancellationToken)
            ?? throw new HttpRequestException("Azure Content Safety returned an empty Prompt Shields response.");

        return body.UserPromptAnalysis?.AttackDetected == true ||
            body.DocumentsAnalysis?.Any(a => a.AttackDetected) == true;
    }

    public async Task<IReadOnlyList<string>> AnalyzeHarmAsync(
        string text,
        int threshold,
        CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(
            $"contentsafety/text:analyze?api-version={ApiVersion}",
            new AnalyzeTextRequest(text, ["Hate", "SelfHarm", "Sexual", "Violence"], "FourSeverityLevels"),
            cancellationToken);

        using var response = await httpClientFactory.CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AnalyzeTextResponse>(cancellationToken)
            ?? throw new HttpRequestException("Azure Content Safety returned an empty text analysis response.");

        return body.CategoriesAnalysis?
            .Where(c => c.Severity >= threshold && !string.IsNullOrWhiteSpace(c.Category))
            .Select(c => c.Category!)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private async Task<HttpRequestMessage> CreateRequestAsync<T>(
        string relativePath,
        T body,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Azure AI Content Safety is not configured.");
        }

        var endpoint = _options.Endpoint!.TrimEnd('/') + "/";
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(endpoint), relativePath))
        {
            Content = JsonContent.Create(body),
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Add("Ocp-Apim-Subscription-Key", _options.ApiKey);
        }
        else
        {
            var token = await _credential.GetTokenAsync(TokenContext, cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        return request;
    }

    private sealed record ShieldPromptRequest(
        [property: JsonPropertyName("userPrompt")] string UserPrompt,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents);

    private sealed record AnalyzeTextRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("categories")] IReadOnlyList<string> Categories,
        [property: JsonPropertyName("outputType")] string OutputType);

    private sealed record ShieldPromptResponse(
        [property: JsonPropertyName("userPromptAnalysis")] AttackAnalysis? UserPromptAnalysis,
        [property: JsonPropertyName("documentsAnalysis")] IReadOnlyList<AttackAnalysis>? DocumentsAnalysis);

    private sealed record AttackAnalysis(
        [property: JsonPropertyName("attackDetected")] bool AttackDetected);

    private sealed record AnalyzeTextResponse(
        [property: JsonPropertyName("categoriesAnalysis")] IReadOnlyList<CategoryAnalysis>? CategoriesAnalysis);

    private sealed record CategoryAnalysis(
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("severity")] int Severity);
}

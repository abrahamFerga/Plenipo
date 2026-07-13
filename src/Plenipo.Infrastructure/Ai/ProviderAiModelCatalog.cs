using System.Net.Http.Headers;
using System.Text.Json;
using Plenipo.Application.Ai;

namespace Plenipo.Infrastructure.Ai;

/// <summary>Fetches provider-owned model catalogs; no model ids are compiled into Plenipo.</summary>
public sealed class ProviderAiModelCatalog(IHttpClientFactory clients) : IAiModelCatalog
{
    public const string HttpClientName = "Plenipo.Ai.ModelCatalog";

    public async Task<AiModelCatalogResult> DiscoverAsync(
        string provider,
        string? endpoint,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        return provider switch
        {
            "OpenAI" => await GetOpenAiCompatibleAsync(
                new Uri("https://api.openai.com/v1/models"), apiKey, cancellationToken),
            "Anthropic" => await GetAnthropicAsync(apiKey, cancellationToken),
            "Ollama" => await GetOpenAiCompatibleAsync(
                ModelsUri(RequireEndpoint(endpoint, provider)), null, cancellationToken),
            "AzureOpenAI" => new([], false,
                "Azure OpenAI uses deployment names. Enter the deployment name configured in your Azure resource."),
            "Mock" => new([], false, "The Mock provider does not expose a model catalog."),
            "None" => new([], false, "Chat is disabled for this tenant."),
            _ => throw new ArgumentException($"Unsupported AI provider '{provider}'.", nameof(provider)),
        };
    }

    private async Task<AiModelCatalogResult> GetOpenAiCompatibleAsync(
        Uri uri, string? apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return new(ReadIds(document.RootElement));
    }

    private async Task<AiModelCatalogResult> GetAnthropicAsync(string? apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1000");
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return new(ReadIds(document.RootElement));
    }

    private static string[] ReadIds(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new HttpRequestException("The provider returned an invalid model catalog.");
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Take(1000)
            .ToArray();
    }

    private static Uri RequireEndpoint(string? endpoint, string provider) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new ArgumentException($"An absolute http(s) endpoint is required for {provider}.", nameof(endpoint));

    private static Uri ModelsUri(Uri endpoint)
    {
        var value = endpoint.AbsoluteUri.TrimEnd('/');
        return new Uri(value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? value + "/models"
            : value + "/v1/models");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (detail.Length > 300)
        {
            detail = detail[..300];
        }

        throw new HttpRequestException(
            $"The provider returned {(int)response.StatusCode} ({response.ReasonPhrase})" +
            (detail.Length == 0 ? "." : $": {detail}"));
    }
}

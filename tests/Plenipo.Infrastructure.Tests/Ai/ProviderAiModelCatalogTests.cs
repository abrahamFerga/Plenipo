using System.Net;
using System.Text;
using Plenipo.Infrastructure.Ai;

namespace Plenipo.Infrastructure.Tests.Ai;

public sealed class ProviderAiModelCatalogTests
{
    [Fact]
    public async Task OpenAI_uses_the_live_models_endpoint_and_returns_sorted_distinct_ids()
    {
        var handler = new RecordingHandler("""
            { "data": [{ "id": "z-model" }, { "id": "a-model" }, { "id": "a-model" }] }
            """);
        var catalog = new ProviderAiModelCatalog(new TestHttpClientFactory(handler));

        var result = await catalog.DiscoverAsync("OpenAI", null, "sk-test", CancellationToken.None);

        Assert.Equal(["a-model", "z-model"], result.Models);
        Assert.Equal("https://api.openai.com/v1/models", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Ollama_uses_its_OpenAI_compatible_models_endpoint_without_a_key()
    {
        var handler = new RecordingHandler("""{ "data": [{ "id": "llama-local" }] }""");
        var catalog = new ProviderAiModelCatalog(new TestHttpClientFactory(handler));

        var result = await catalog.DiscoverAsync(
            "Ollama", "http://localhost:11434/v1", null, CancellationToken.None);

        Assert.Equal(["llama-local"], result.Models);
        Assert.Equal("http://localhost:11434/v1/models", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Null(handler.Request.Headers.Authorization);
    }

    [Fact]
    public async Task Azure_explains_that_the_model_is_a_deployment_name()
    {
        var catalog = new ProviderAiModelCatalog(new TestHttpClientFactory(new RecordingHandler("{}")));

        var result = await catalog.DiscoverAsync(
            "AzureOpenAI", "https://example.openai.azure.com", null, CancellationToken.None);

        Assert.False(result.SupportsDiscovery);
        Assert.Empty(result.Models);
        Assert.Contains("deployment name", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}

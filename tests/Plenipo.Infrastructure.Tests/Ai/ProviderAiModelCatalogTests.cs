using System.Net;
using System.Text;
using Plenipo.Infrastructure.Ai;

namespace Plenipo.Infrastructure.Tests.Ai;

public sealed class ProviderAiModelCatalogTests
{
    [Fact]
    public async Task OpenAI_uses_the_live_models_endpoint_and_returns_sorted_distinct_ids()
    {
        // Chat ids on purpose: the OpenAI catalog is narrowed to chat-capable models, so this test
        // uses ids that survive that in order to keep proving what it is here for — sorting, dedup,
        // the exact URL, and the bearer key.
        var handler = new RecordingHandler("""
            { "data": [{ "id": "gpt-4o" }, { "id": "chatgpt-4o-latest" }, { "id": "gpt-4o" }] }
            """);
        var catalog = new ProviderAiModelCatalog(new TestHttpClientFactory(handler));

        var result = await catalog.DiscoverAsync("OpenAI", null, "sk-test", CancellationToken.None);

        Assert.Equal(["chatgpt-4o-latest", "gpt-4o"], result.Models);
        Assert.Equal("https://api.openai.com/v1/models", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", handler.Request.Headers.Authorization.Parameter);
        // Nothing was hidden, so there is nothing to report.
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task OpenAI_catalog_offers_only_chat_completion_models()
    {
        // The real shape of the problem: one account-wide catalog spanning every modality OpenAI
        // sells. An admin picking a model for a chat assistant should not be offered dall-e-3.
        var handler = new RecordingHandler("""
            { "data": [
                { "id": "gpt-4o" }, { "id": "gpt-4o-mini" }, { "id": "o3-mini" },
                { "id": "dall-e-3" }, { "id": "whisper-1" }, { "id": "tts-1-hd" },
                { "id": "text-embedding-3-large" }, { "id": "omni-moderation-latest" },
                { "id": "babbage-002" }, { "id": "gpt-3.5-turbo-instruct" }
            ] }
            """);
        var catalog = new ProviderAiModelCatalog(new TestHttpClientFactory(handler));

        var result = await catalog.DiscoverAsync("OpenAI", null, "sk-test", CancellationToken.None);

        Assert.Equal(["gpt-4o", "gpt-4o-mini", "o3-mini"], result.Models);
    }

    [Fact]
    public async Task OpenAI_says_how_many_models_it_hid_instead_of_narrowing_silently()
    {
        // A narrowed list with no note reads as "this is everything your account has", and the
        // admin never learns that typing an id by hand is still open to them.
        var handler = new RecordingHandler("""
            { "data": [{ "id": "gpt-4o" }, { "id": "dall-e-3" }, { "id": "whisper-1" }] }
            """);
        var catalog = new ProviderAiModelCatalog(new TestHttpClientFactory(handler));

        var result = await catalog.DiscoverAsync("OpenAI", null, "sk-test", CancellationToken.None);

        Assert.Equal(["gpt-4o"], result.Models);
        Assert.NotNull(result.Message);
        Assert.Contains("2 non-chat", result.Message);
        Assert.Contains("by hand", result.Message);
    }

    [Fact]
    public async Task Ollama_is_never_filtered_by_OpenAIs_naming_rules()
    {
        // Guard, not a regression test — green before and after. The chat-family gate is
        // default-deny, so leaking it into the shared id reader would empty every Ollama catalog.
        var handler = new RecordingHandler("""
            { "data": [{ "id": "llama3.1" }, { "id": "nomic-embed-text" }, { "id": "qwen2.5" }] }
            """);
        var catalog = new ProviderAiModelCatalog(new TestHttpClientFactory(handler));

        var result = await catalog.DiscoverAsync(
            "Ollama", "http://localhost:11434/v1", null, CancellationToken.None);

        Assert.Equal(["llama3.1", "nomic-embed-text", "qwen2.5"], result.Models);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task Anthropic_is_passed_through_unfiltered()
    {
        // Guard. Anthropic's catalog is all chat models, so there is nothing to narrow — and its
        // ids would not survive OpenAI's family gate either.
        var handler = new RecordingHandler("""
            { "data": [{ "id": "claude-sonnet-4-5" }, { "id": "claude-haiku-4-5" }] }
            """);
        var catalog = new ProviderAiModelCatalog(new TestHttpClientFactory(handler));

        var result = await catalog.DiscoverAsync("Anthropic", null, "sk-ant", CancellationToken.None);

        Assert.Equal(["claude-haiku-4-5", "claude-sonnet-4-5"], result.Models);
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

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Plenipo.Application.Ai;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// When the provider rejects the tenant's key mid-turn, the chat stream has to say so. The person who
/// can fix it is the tenant's administrator, and on a hosted deployment they cannot read the host log
/// where the real reason is written — so if the stream does not carry it, nobody actionable ever learns
/// it. This drives the failure through the whole runner rather than the classifier alone, because the
/// bug being fixed was the runner discarding the classification, not the classification being wrong.
/// </summary>
public sealed class AgentTurnFailureReportingTests : IClassFixture<AgentTurnFailureReportingTests.RejectedKeyFactory>
{
    private readonly RejectedKeyFactory _factory;

    public AgentTurnFailureReportingTests(RejectedKeyFactory factory) => _factory = factory;

    [Fact]
    public async Task A_key_the_provider_rejected_is_reported_as_a_key_problem()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "turn-failure");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");

        var response = await client.PostAsJsonAsync("/api/chat/stream", new { moduleId = "test", message = "hello" });
        response.EnsureSuccessStatusCode();
        var events = (await response.Content.ReadFromJsonAsync<List<StreamEvent>>())!;

        var failure = Assert.Single(events, e => e.Type == "Error");

        // The turn failed for a reason the administrator can act on, and the message says which screen.
        Assert.Contains("API key", failure.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AI settings", failure.Error, StringComparison.OrdinalIgnoreCase);
        // The regression: before this fix every provider failure collapsed to one unactionable string.
        Assert.NotEqual("The assistant could not complete the request.", failure.Error);
    }

    [Fact]
    public async Task The_providers_own_error_text_is_not_relayed_to_the_caller()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "turn-failure-leak");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");

        var response = await client.PostAsJsonAsync("/api/chat/stream", new { moduleId = "test", message = "hello" });
        var events = (await response.Content.ReadFromJsonAsync<List<StreamEvent>>())!;

        var failure = Assert.Single(events, e => e.Type == "Error");

        Assert.DoesNotContain(RejectedKeyResolver.LeakyProviderMessage, failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-4f9c", failure.Error, StringComparison.Ordinal);
    }

    private sealed record StreamEvent(string Type, string? Text, string? ToolName, Guid? ConversationId, string? Error);

    /// <summary>The platform host with every turn running on a provider that rejects the key.</summary>
    public sealed class RejectedKeyFactory : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // Replace the resolver rather than the IChatClient: the runner asks the resolver for the
            // turn's client, so this is the seam a real misconfigured connection actually flows through.
            builder.ConfigureServices(services =>
                services.AddSingleton<ITenantChatClientResolver>(new RejectedKeyResolver()));
        }
    }

    private sealed class RejectedKeyResolver : ITenantChatClientResolver
    {
        /// <summary>Shaped like a real provider 401, which quotes the key and the org back at you.</summary>
        internal const string LeakyProviderMessage =
            "Incorrect API key sk-live-4f9c provided for org-acme. You can find your API key at https://api.example.com/account/api-keys";

        public Task<IChatClient?> ResolveAsync(
            EffectiveAiSettings settings, string? modelOverride, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient?>(new RejectingChatClient());
    }

    /// <summary>Fails the way a live provider does: on the first read of the stream, not at construction.</summary>
    private sealed class RejectingChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new HttpRequestException(
                RejectedKeyResolver.LeakyProviderMessage, inner: null, HttpStatusCode.Unauthorized);
#pragma warning disable CS0162 // Unreachable — required for the compiler to treat this as an iterator.
            yield break;
#pragma warning restore CS0162
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException(
                RejectedKeyResolver.LeakyProviderMessage, inner: null, HttpStatusCode.Unauthorized);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

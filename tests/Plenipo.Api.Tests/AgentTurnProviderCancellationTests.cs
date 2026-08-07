using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Plenipo.Application.Ai;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The complement of <see cref="AgentTurnCallerAbortTests"/>, and the direction that actually pins the
/// runner's <c>when (cancellationToken.IsCancellationRequested)</c> guard.
/// <para>
/// Both directions have to hold, but only this one distinguishes the guard from the code it replaced:
/// with the predicate deleted, a caller abort still rethrows, so the abort test stays green and the
/// change looks untested. What the predicate really buys is this case — a cancellation the caller did
/// NOT ask for, which is how <c>HttpClient</c> reports its own read timeout. Before the fix that
/// propagated out of the runner and tore the stream down with no event at all, so the chat window went
/// silent and the tenant was told nothing.
/// </para>
/// </summary>
public sealed class AgentTurnProviderCancellationTests
    : IClassFixture<AgentTurnProviderCancellationTests.TimingOutProviderFactory>
{
    private readonly TimingOutProviderFactory _factory;

    public AgentTurnProviderCancellationTests(TimingOutProviderFactory factory) => _factory = factory;

    [Fact]
    public async Task A_provider_read_that_times_out_is_reported_rather_than_tearing_the_stream_down()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "provider-timeout");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");

        // No CancellationToken is passed: the caller never aborts, so any cancellation reaching the
        // runner came from the provider read and is not the caller's to be blamed for.
        var response = await client.PostAsJsonAsync(
            "/api/chat/stream", new { moduleId = "test", message = "hello" });

        response.EnsureSuccessStatusCode();
        var events = (await response.Content.ReadFromJsonAsync<List<StreamEvent>>())!;

        // The regression this pins: the turn used to end with no Error event whatsoever.
        var failure = Assert.Single(events, e => e.Type == "Error");

        // Reported as a provider timeout, not as the opaque string the fix exists to narrow.
        Assert.Contains("did not respond in time", failure.Error, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("The assistant could not complete the request.", failure.Error);

        // Guard against a vacuous pass — the provider must really have been read for the catch under
        // test to have been reached at all.
        Assert.True(
            await TimingOutChatClient.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30)),
            "the provider read never started, so the classification path was not exercised");
    }

    private sealed record StreamEvent(string Type, string? Text, string? ToolName, Guid? ConversationId, string? Error);

    /// <summary>The platform host on a provider whose own read clock runs out mid-turn.</summary>
    public sealed class TimingOutProviderFactory : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
                services.AddSingleton<ITenantChatClientResolver>(new TimingOutResolver()));
        }
    }

    private sealed class TimingOutResolver : ITenantChatClientResolver
    {
        public Task<IChatClient?> ResolveAsync(
            EffectiveAiSettings settings, string? modelOverride, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient?>(new TimingOutChatClient());
    }

    /// <summary>
    /// Fails exactly as <c>HttpClient</c> does when its own <c>Timeout</c> elapses: a
    /// <see cref="TaskCanceledException"/> carrying a <see cref="TimeoutException"/>, raised while the
    /// request's token is still uncancelled.
    /// </summary>
    private sealed class TimingOutChatClient : IChatClient
    {
        internal static readonly TaskCompletionSource<bool> ReadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            ReadStarted.TrySetResult(true);
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
                new TimeoutException("A connection could not be established within the configured timeout."));
#pragma warning disable CS0162 // Unreachable — required for the compiler to treat this as an iterator.
            yield break;
#pragma warning restore CS0162
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new TaskCanceledException("timed out", new TimeoutException("timed out"));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

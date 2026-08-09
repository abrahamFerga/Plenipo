using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plenipo.Application.Ai;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// Classifying a failed turn made one behavioural change to the runner: a cancellation is only treated
/// as a caller abort when the turn's OWN token is the source, and anything else reports as a provider
/// failure. That predicate is the single piece of control flow in the fix, and it has to be right in
/// both directions — the reporting direction is covered by
/// <see cref="AgentTurnFailureReportingTests"/>; this covers the abort direction.
/// <para>
/// Getting it wrong the other way is the expensive mistake: a user who closes the tab, or a host
/// shutting down, would have their own cancellation logged as an error and reported to the tenant as
/// "the AI provider did not respond" — inventing a provider incident out of a user walking away.
/// </para>
/// </summary>
public sealed class AgentTurnCallerAbortTests : IClassFixture<AgentTurnCallerAbortTests.HangingProviderFactory>
{
    private readonly HangingProviderFactory _factory;

    public AgentTurnCallerAbortTests(HangingProviderFactory factory) => _factory = factory;

    [Fact]
    public async Task A_caller_who_aborts_the_turn_is_not_reported_as_a_provider_failure()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "caller-abort");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");

        using var abort = new CancellationTokenSource();
        var turn = client.PostAsJsonAsync(
            "/api/chat/stream", new { moduleId = "test", message = "hello" }, abort.Token);

        // Only abort once the provider read is genuinely in flight — otherwise the turn could fail
        // somewhere before the catch under test and the assertions below would pass vacuously.
        await HangingChatClient.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);

        // The guard against a vacuous pass: the runner really did reach the provider and the abort
        // really did arrive on the turn's own token, which is the branch being asserted.
        Assert.True(
            await HangingChatClient.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(30)),
            "the provider read never observed the caller's cancellation, so the abort branch was not exercised");

        // The runner rethrew rather than classifying. Both halves of "classified" are absent: the
        // error log the reporting path always writes, and the message it would have written.
        Assert.DoesNotContain(_factory.LogMessages, message =>
            message.Contains("Agent turn failed", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.LogMessages, message =>
            message.Contains("did not respond in time", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The platform host on a provider that never answers, so the caller is the one who ends the turn.</summary>
    public sealed class HangingProviderFactory : PlenipoApiFactory
    {
        private readonly RecordingLoggerProvider _logs = new();

        public IReadOnlyCollection<string> LogMessages => _logs.Messages;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ITenantChatClientResolver>(new HangingResolver());
                services.AddSingleton<ILoggerProvider>(_logs);
            });
        }
    }

    private sealed class HangingResolver : ITenantChatClientResolver
    {
        public Task<IChatClient?> ResolveAsync(
            EffectiveAiSettings settings, string? modelOverride, CancellationToken cancellationToken = default) =>
            Task.FromResult<IChatClient?>(new HangingChatClient());
    }

    /// <summary>Answers nothing and waits, the way a provider holding a connection open does.</summary>
    private sealed class HangingChatClient : IChatClient
    {
        internal static readonly TaskCompletionSource ReadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static readonly TaskCompletionSource<bool> CancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The turn's own token — exactly the case the runner must treat as a caller abort.
                CancellationObserved.TrySetResult(true);
                throw;
            }

            yield break;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<ChatResponse>(
                _ => throw new OperationCanceledException(cancellationToken), TaskScheduler.Default);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Captures what the host logged, so the test can assert on the branch the runner took.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}

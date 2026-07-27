using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Plenipo.Application.Ai;
using Plenipo.Infrastructure.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The hand-back half of human-in-the-loop. The blocking half tells the model a side-effecting tool
/// was "NOT executed, pending approval" — and that statement lives on in the conversation record.
/// When a human later resolves the approval OUTSIDE the chat (the approvals endpoints), the runner
/// must report the outcome into the conversation's next turn, or the assistant keeps claiming the
/// action is pending forever, whatever actually happened. These tests drive the real endpoints and
/// assert on the exact model INPUT via a capturing chat client — not on canned reply text.
/// </summary>
public sealed class ApprovalOutcomeConversationTests : IClassFixture<ApprovalOutcomeConversationTests.CapturingApiFactory>
{
    private readonly CapturingApiFactory _factory;

    public ApprovalOutcomeConversationTests(CapturingApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task An_approved_execution_reaches_the_next_turn_and_is_reported_once()
    {
        var client = ClientAs("outcome-approver");
        var capture = _factory.Capture;

        // Turn 1: the agent attempts the side-effecting tool; the platform blocks it pending approval.
        var conversationId = await RunTurnAsync(client, null, "please use the record tool with 'ledger-entry'");
        var approval = await FindApprovalAsync(client, conversationId);

        // A human approves OUTSIDE the chat; the tool really runs (TestToolSource: "recorded: …").
        var approve = await client.PostAsync($"/api/chat/approvals/{approval}/approve", null);
        approve.EnsureSuccessStatusCode();

        // The click is not silent: the response echoes the outcome note, and the SAME note is now
        // a persisted assistant message in the transcript — the user sees what happened without
        // having to ask, and a reload agrees with what the click showed.
        var resolution = await approve.Content.ReadFromJsonAsync<JsonElement>();
        var note = resolution.GetProperty("note").GetString();
        Assert.Contains("✅ Approved by", note);
        Assert.Contains("recorded: ledger-entry", note);
        var transcript = await client.GetFromJsonAsync<List<JsonElement>>($"/api/chat/conversations/{conversationId}/messages");
        var lastMessage = transcript!.Last();
        Assert.Equal("Assistant", lastMessage.GetProperty("role").GetString());
        Assert.Equal(note, lastMessage.GetProperty("content").GetString());

        // Turn 2: the model's NEW input must carry the outcome — tool name, decision, and actual
        // result — superseding the stale "NOT executed" tool result from turn 1.
        await RunTurnAsync(client, conversationId, "did that go through?");
        var seen = NewestUserMessage(capture);
        Assert.Contains("[Approval outcomes]", seen);
        Assert.Contains("'record': APPROVED", seen);
        Assert.Contains("recorded: ledger-entry", seen);

        // Turn 3: the outcome was delivered by a completed turn — the NEW user message must not
        // repeat it. (The session history legitimately still contains turn 2's composed message.)
        await RunTurnAsync(client, conversationId, "thanks!");
        Assert.DoesNotContain("[Approval outcomes]", NewestUserMessage(capture));
    }

    [Fact]
    public async Task A_rejection_reaches_the_next_turn()
    {
        var client = ClientAs("outcome-rejecter");
        var capture = _factory.Capture;

        var conversationId = await RunTurnAsync(client, null, "please use the record tool with 'denied-entry'");
        var approval = await FindApprovalAsync(client, conversationId);

        var reject = await client.PostAsync($"/api/chat/approvals/{approval}/reject", null);
        reject.EnsureSuccessStatusCode();

        // The rejection is written into the transcript too — declining is also an answer.
        var transcript = await client.GetFromJsonAsync<List<JsonElement>>($"/api/chat/conversations/{conversationId}/messages");
        Assert.Contains("🚫 Rejected by", transcript!.Last().GetProperty("content").GetString());

        await RunTurnAsync(client, conversationId, "so, what happened?");
        var seen = NewestUserMessage(capture);
        Assert.Contains("'record': REJECTED", seen);
        Assert.Contains("Do not retry", seen);
    }

    [Fact]
    public async Task The_requester_is_notified_when_someone_else_decides()
    {
        using var requester = ClientAs("outcome-requester");
        using var resolver = ClientAs("outcome-third-party");
        (await resolver.GetAsync("/api/platform/me")).EnsureSuccessStatusCode(); // JIT-provision

        var conversationId = await RunTurnAsync(requester, null, "please use the record tool with 'team-entry'");
        var approval = await FindApprovalAsync(requester, conversationId);

        var approve = await resolver.PostAsync($"/api/chat/approvals/{approval}/approve", null);
        approve.EnsureSuccessStatusCode();

        // The requester — who did NOT click — gets one inbox ping with the outcome; the resolver
        // watched it happen and gets none (self-echo would be noise).
        var inbox = await requester.GetFromJsonAsync<List<JsonElement>>("/api/notifications");
        var ping = Assert.Single(inbox!.Where(n =>
            n.GetProperty("category").GetString() == "test.approvals"
            && n.GetProperty("title").GetString() == "Approved and ran: record"));
        Assert.Contains("✅ Approved by", ping.GetProperty("body").GetString());

        var resolverInbox = await resolver.GetFromJsonAsync<List<JsonElement>>("/api/notifications");
        Assert.DoesNotContain(resolverInbox!, n => n.GetProperty("title").GetString() == "Approved and ran: record");
    }

    /// <summary>Runs one chat turn and returns the conversation id from the Completed event.</summary>
    private static async Task<Guid> RunTurnAsync(HttpClient client, Guid? conversationId, string message)
    {
        var response = await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", conversationId, message });
        response.EnsureSuccessStatusCode();
        var events = (await response.Content.ReadFromJsonAsync<List<StreamEvent>>())!;
        var completed = events.Last(e => e.Type == "Completed");
        return completed.ConversationId!.Value;
    }

    private static async Task<Guid> FindApprovalAsync(HttpClient client, Guid conversationId)
    {
        var approvals = await client.GetFromJsonAsync<List<ApprovalDto>>("/api/chat/approvals");
        return approvals!.Single(a => a.ConversationId == conversationId && a.ToolName == "record").Id;
    }

    /// <summary>
    /// The newest USER message the chat client received — the current turn's composed input. Session
    /// history precedes it in the call, so asserting here pins what THIS turn injected, not what
    /// earlier turns already carried.
    /// </summary>
    private static string NewestUserMessage(CapturingChatClient capture) =>
        capture.Calls.Last().Last(m => m.Role == ChatRole.User).Text;

    private sealed record StreamEvent(string Type, string? Text, string? ToolName, Guid? ConversationId, string? Error);

    private sealed record ApprovalDto(Guid Id, Guid ConversationId, string ModuleId, string ToolName);

    /// <summary>
    /// The API factory with the chat client wrapped so tests can assert on the exact messages the
    /// model received — the only reliable way to pin "the outcome reached the model". The wrap
    /// happens at the <see cref="ITenantChatClientResolver"/> seam because <c>ChatClientFactory</c>
    /// constructs provider clients itself — the DI-registered <c>IChatClient</c> is not on the
    /// turn path.
    /// </summary>
    public sealed class CapturingApiFactory : PlenipoApiFactory
    {
        public CapturingChatClient Capture { get; } = new(new MockChatClient());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll(typeof(ITenantChatClientResolver));
                services.AddSingleton<ITenantChatClientResolver>(new FixedResolver(Capture));
            });
        }

        private sealed class FixedResolver(IChatClient client) : ITenantChatClientResolver
        {
            public Task<IChatClient?> ResolveAsync(
                EffectiveAiSettings settings, string? modelOverride, CancellationToken cancellationToken = default) =>
                Task.FromResult<IChatClient?>(client);
        }
    }

    /// <summary>Forwards to the inner client, recording a snapshot of every call's input messages.</summary>
    public sealed class CapturingChatClient(IChatClient inner) : IChatClient
    {
        private readonly List<IReadOnlyList<ChatMessage>> _calls = [];

        public IReadOnlyList<IReadOnlyList<ChatMessage>> Calls
        {
            get
            {
                lock (_calls)
                {
                    return [.. _calls];
                }
            }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToList();
            lock (_calls)
            {
                _calls.Add(snapshot);
            }

            await foreach (var update in inner.GetStreamingResponseAsync(snapshot, options, cancellationToken))
            {
                yield return update;
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToList();
            lock (_calls)
            {
                _calls.Add(snapshot);
            }

            return inner.GetResponseAsync(snapshot, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);

        public void Dispose() => inner.Dispose();
    }
}

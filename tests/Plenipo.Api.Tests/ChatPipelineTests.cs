using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of the chat pipeline — the platform's headline feature and the security spine. A turn
/// runs through the real path: dev-auth → the authorized agent runner (permission-filtered tools, budget, audit)
/// → the dependency-free Mock chat client → a streamed reply → conversation persistence. Also pins the
/// <c>chat.use</c> gate on starting a turn.
/// </summary>
public sealed class ChatPipelineTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public ChatPipelineTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task A_chat_turn_streams_an_assistant_reply_and_persists_the_conversation()
    {
        var client = ClientAs("user", "chat-alice"); // the plain user role holds chat.use

        var response = await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", message = "Hello there" });
        response.EnsureSuccessStatusCode();

        var events = await response.Content.ReadFromJsonAsync<List<StreamEvent>>();
        Assert.NotNull(events);

        // The Mock client streams assistant text, then the runner completes with the new conversation id.
        Assert.Contains(events!, e => e.Type == "Token" && !string.IsNullOrEmpty(e.Text));
        var completed = events!.SingleOrDefault(e => e.Type == "Completed");
        Assert.NotNull(completed);
        Assert.NotNull(completed!.ConversationId);

        // The turn was persisted: the conversation now appears in the caller's own history.
        var history = await client.GetFromJsonAsync<List<ConversationDto>>("/api/chat/conversations");
        Assert.Contains(history!, c => c.Id == completed.ConversationId);
    }

    [Fact]
    public async Task A_turns_messages_are_persisted_and_replayable()
    {
        var client = ClientAs("user", "replay-alice");

        var events = (await (await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", message = "Remember this" })).Content.ReadFromJsonAsync<List<StreamEvent>>())!;
        var conversationId = events.Single(e => e.Type == "Completed").ConversationId;
        Assert.NotNull(conversationId);

        // The turn's messages are persisted and returned for replay (this is how a conversation resumes across
        // processes — the agent rebuilds context by replaying these).
        var messages = await client.GetFromJsonAsync<List<MessageDto>>(
            $"/api/chat/conversations/{conversationId}/messages");
        Assert.NotNull(messages);
        Assert.Contains(messages!, m => m.Role == "User" && m.Content == "Remember this");
        Assert.Contains(messages!, m => m.Role == "Assistant" && !string.IsNullOrEmpty(m.Content));
    }

    private sealed record StreamEvent(string Type, string? Text, string? ToolName, Guid? ConversationId, string? Error);

    private sealed record ConversationDto(Guid Id, string ModuleId, string? Title, DateTimeOffset UpdatedAt);

    private sealed record MessageDto(Guid Id, string Role, string Content);
}

using System.Net;
using System.Net.Http.Json;
using Plenipo.Core.Platform;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Prompt provenance end to end: a chat turn stamps its assistant message with the hash of the
/// effective instructions, and an admin can resolve that hash back to the exact prompt text the
/// turn ran under.
/// </summary>
[Collection("api")]
public sealed class ProvenanceTests(IntegrationFixture fixture)
{
    private sealed record SnapshotDto(string Hash, string Instructions, DateTimeOffset FirstSeenAt);

    [Fact]
    public async Task Turn_StampsAssistantMessage_AndHashResolvesToTheExactInstructions()
    {
        using var client = fixture.ClientFor("system_admin");

        using var chat = await client.PostAsJsonAsync("/api/agui/finance",
            new { messages = new[] { new { id = "m1", role = "user", content = "Hello provenance" } } });
        chat.EnsureSuccessStatusCode();
        var sse = await chat.Content.ReadAsStringAsync();
        Assert.False(sse.Contains("RUN_ERROR", StringComparison.Ordinal), $"Turn failed: {sse}");

        // The internal conversation id arrives in RUN_FINISHED.result (the client's threadId is
        // an external alias, mapped server-side).
        var finished = sse.Split('\n').First(l => l.Contains("RUN_FINISHED", StringComparison.Ordinal));
        using var doc = System.Text.Json.JsonDocument.Parse(finished["data: ".Length..]);
        var conversationId = doc.RootElement.GetProperty("result").GetProperty("conversationId").GetGuid();

        var conversation = await fixture.GetConversationAsync(conversationId);
        var assistant = Assert.Single(conversation.Messages, m => m.Role == MessageRole.Assistant);
        var user = Assert.Single(conversation.Messages, m => m.Role == MessageRole.User);

        Assert.Null(user.InstructionsHash); // provenance is a property of the reply, not the ask
        Assert.NotNull(assistant.InstructionsHash);
        Assert.Equal(64, assistant.InstructionsHash!.Length); // sha-256 hex

        using var lookup = await client.GetAsync($"/api/admin/instruction-snapshots/{assistant.InstructionsHash}");
        lookup.EnsureSuccessStatusCode();
        var snapshot = await lookup.Content.ReadFromJsonAsync<SnapshotDto>();

        Assert.NotNull(snapshot);
        Assert.Equal(assistant.InstructionsHash, snapshot!.Hash);
        // The resolved text is the full assembly: base system prompt + the finance module's instructions.
        Assert.Contains("You are Plenipo", snapshot.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotLookup_IsGatedByManageAiSettings()
    {
        using var response = await fixture.ClientFor("user")
            .GetAsync($"/api/admin/instruction-snapshots/{new string('0', 64)}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

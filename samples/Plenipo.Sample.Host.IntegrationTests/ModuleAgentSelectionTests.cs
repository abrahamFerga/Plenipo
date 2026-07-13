using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Sample.Host.IntegrationTests.Evals;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Module-shipped agents end to end (the Claude-Code-style per-turn composition): the Legal
/// manifest declares "drafter" and "docketing"; a turn picks one via AG-UI forwardedProps and gets
/// that agent's instructions + narrowed tool surface. Unknown agents and unadvertised models fail
/// the run readably — never a silent fallback.
/// </summary>
[Collection("api")]
public sealed class ModuleAgentSelectionTests(IntegrationFixture fixture)
{
    // Only "deadlines" is a matching token (>=4 chars): the Mock provider routes it to
    // list_deadlines deterministically. Mentioning "matters" would tie with list_matters.
    private const string DeadlineQuestion = "What deadlines are coming up soon?";

    [Fact]
    public async Task ModulesPayload_ListsManifestAgents()
    {
        var modules = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var agents = modules.EnumerateArray().First(m => m.GetProperty("id").GetString() == "legal")
            .GetProperty("agents").EnumerateArray().ToArray();

        var drafter = agents.Single(a => a.GetProperty("name").GetString() == "drafter");
        Assert.Contains("drafting", drafter.GetProperty("description").GetString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains(agents, a => a.GetProperty("name").GetString() == "docketing");
    }

    [Fact]
    public async Task PickedAgent_NarrowsTheToolSurface_ControlDoesNot()
    {
        using var admin = fixture.ClientFor("system_admin");

        // Control: no agent picked → the deadline question routes to A deadline tool (which one
        // depends on the Mock's tie-break by declaration order — any of them proves reachability).
        var control = EvalRun.Parse(await ChatAsync(admin, DeadlineQuestion, agent: null));
        Assert.Contains(control.ToolCalls, t => t.Contains("deadline", StringComparison.Ordinal));

        // The drafter agent excludes every deadline tool — same question, no such call possible.
        var drafter = EvalRun.Parse(await ChatAsync(admin, DeadlineQuestion, agent: "drafter"));
        Assert.DoesNotContain("RUN_ERROR", drafter.EventTypes);
        Assert.DoesNotContain(drafter.ToolCalls, t => t.Contains("deadline", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownAgent_FailsTheRunReadably()
    {
        using var admin = fixture.ClientFor("system_admin");
        var run = EvalRun.Parse(await ChatAsync(admin, "Hello", agent: "does-not-exist"));

        Assert.Contains("RUN_ERROR", run.EventTypes);
        // Apostrophes arrive JSON-escaped (') in the raw SSE, so assert the parts.
        Assert.Contains("Unknown agent", run.RawSse);
        Assert.Contains("does-not-exist", run.RawSse);
    }

    [Fact]
    public async Task TenantProfile_OverridesTheManifestAgentOfTheSameName()
    {
        using var admin = fixture.ClientFor("system_admin");

        // An admin retasks "drafter" with a profile of the same name that allows the deadline tools.
        using var created = await admin.PutAsJsonAsync("/api/admin/agent-profiles", new
        {
            moduleId = "legal",
            name = "drafter",
            instructions = "You handle everything, including deadlines.",
            mode = "Append",
            isDefault = false,
            toolNames = new[] { "list_deadlines", "list_matters" },
        });
        created.EnsureSuccessStatusCode();
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var profileId = createdBody.RootElement.GetProperty("id").GetGuid();

        try
        {
            // The same pick now resolves to the tenant profile — deadline tools are back (only
            // list_deadlines is in the profile's selection, so no tie is possible).
            var run = EvalRun.Parse(await ChatAsync(admin, DeadlineQuestion, agent: "drafter"));
            Assert.Contains("list_deadlines", run.ToolCalls);
        }
        finally
        {
            (await admin.DeleteAsync($"/api/admin/agent-profiles/{profileId}")).EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task ModelPick_HonoursTheAdvertisedListOnly()
    {
        using var admin = fixture.ClientFor("system_admin");

        // "mock-thorough" is on Ai:AvailableModels — the turn runs (Mock ignores the model itself).
        var allowed = EvalRun.Parse(await ChatAsync(admin, "Hello there, assistant", model: "mock-thorough"));
        Assert.DoesNotContain("RUN_ERROR", allowed.EventTypes);
        Assert.Contains("RUN_FINISHED", allowed.EventTypes);

        // An arbitrary model string is refused before any client is built.
        var refused = EvalRun.Parse(await ChatAsync(admin, "Hello there, assistant", model: "gpt-not-a-thing"));
        Assert.Contains("RUN_ERROR", refused.EventTypes);
        Assert.Contains("not available", refused.RawSse);
    }

    private static async Task<string> ChatAsync(
        HttpClient client, string message, string? agent = null, string? model = null)
    {
        using var chat = await client.PostAsJsonAsync("/api/agui/legal", new
        {
            messages = new[] { new { id = "m1", role = "user", content = message } },
            forwardedProps = new { agent, model },
        });
        chat.EnsureSuccessStatusCode();
        return await chat.Content.ReadAsStringAsync();
    }
}

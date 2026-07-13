using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Plenipo.Sample.Host.IntegrationTests.Evals;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Module-shipped skills end to end: the Legal module bundles "engagement-letter" (manifest
/// SkillsPath → module-skills/legal in the host output), advertised and slash-invocable ONLY in
/// Legal's chat; the global library (brand-voice) shows everywhere. A "/skill …" turn is rewritten
/// into a load-and-follow instruction for the model, while the transcript keeps what was typed.
/// </summary>
[Collection("api")]
public sealed class ModuleSkillTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task ModuleSkills_AreScopedToTheirModule_GlobalsShowEverywhere()
    {
        var modules = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");

        string[] SkillsOf(string id) => modules.EnumerateArray()
            .First(m => m.GetProperty("id").GetString() == id)
            .GetProperty("skills").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()!)
            .ToArray();

        var legal = SkillsOf("legal");
        var finance = SkillsOf("finance");

        Assert.Contains("engagement-letter", legal);
        Assert.DoesNotContain("engagement-letter", finance); // module-scoped
        Assert.Contains("brand-voice", legal);               // the global library shows everywhere
        Assert.Contains("brand-voice", finance);
    }

    [Fact]
    public async Task SlashTurn_LoadsTheSkill_ButTheTranscriptKeepsWhatWasTyped()
    {
        using var admin = fixture.ClientFor("system_admin");
        const string typed = "/engagement-letter for the Vandelay acquisition";

        using var chat = await admin.PostAsJsonAsync("/api/agui/legal",
            new { messages = new[] { new { id = "m1", role = "user", content = typed } } });
        chat.EnsureSuccessStatusCode();
        var run = EvalRun.Parse(await chat.Content.ReadAsStringAsync());

        // The rewrite steers the model to load the skill (the Mock provider matches load_skill).
        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
        Assert.Contains("load_skill", run.ToolCalls);

        // The persisted user message is the slash command itself — history shows what was typed.
        var conversationId = Regex.Match(run.RawSse, "\"conversationId\":\"([0-9a-f-]{36})\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(conversationId));
        var messages = await admin.GetFromJsonAsync<JsonElement>($"/api/chat/conversations/{conversationId}/messages");
        var user = messages.EnumerateArray().First(m => m.GetProperty("role").GetString() == "User");
        Assert.Equal(typed, user.GetProperty("content").GetString());
    }

    [Fact]
    public async Task SlashWithUnknownName_PassesThroughUntouched()
    {
        using var admin = fixture.ClientFor("system_admin");
        using var chat = await admin.PostAsJsonAsync("/api/agui/legal",
            new { messages = new[] { new { id = "m1", role = "user", content = "/frobnicate hello there" } } });
        chat.EnsureSuccessStatusCode();
        var run = EvalRun.Parse(await chat.Content.ReadAsStringAsync());

        // Not a skill → an ordinary message; no rewrite, no forced skill load, no error.
        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
        Assert.DoesNotContain("load_skill", run.ToolCalls);
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Sample.Host.IntegrationTests.Evals;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Agent composition end to end: a default profile's tool selection narrows what the model can
/// call — the same question that routes to a finance tool when unrestricted produces no such tool
/// call once the profile excludes it. RBAC still gates everything; the selection only narrows.
/// </summary>
[Collection("api")]
public sealed class AgentToolSelectionTests(IntegrationFixture fixture)
{
    private const string Question = "Summarize my spending on groceries this month";

    [Fact]
    public async Task DefaultProfileToolSelection_NarrowsTheModelsToolSurface()
    {
        using var admin = fixture.ClientFor("system_admin");

        // Control: unrestricted, the spending question routes to the finance summary tool.
        var control = EvalRun.Parse(await ChatAsync(admin));
        Assert.Contains("summarize_spending", control.ToolCalls);

        // Restrict the finance agent to the skill loop only.
        using var created = await admin.PutAsJsonAsync("/api/admin/agent-profiles", new
        {
            moduleId = "finance",
            name = "narrow-tools-eval",
            instructions = "Answer briefly.",
            mode = "Append",
            isDefault = true,
            toolNames = new[] { "load_skill" },
        });
        created.EnsureSuccessStatusCode();
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var profileId = createdBody.RootElement.GetProperty("id").GetGuid();

        try
        {
            // The selection round-trips through the read API (the admin UI depends on it).
            using var listed = await admin.GetAsync("/api/admin/agent-profiles?moduleId=finance");
            listed.EnsureSuccessStatusCode();
            Assert.Contains("load_skill", await listed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // Same question, but the excluded tool is no longer in the model's tool surface.
            var restricted = EvalRun.Parse(await ChatAsync(admin));
            Assert.DoesNotContain("RUN_ERROR", restricted.EventTypes);
            Assert.DoesNotContain("summarize_spending", restricted.ToolCalls);
        }
        finally
        {
            // Never leak a default profile into the shared fixture — the eval suite runs finance cases.
            using var deleted = await admin.DeleteAsync($"/api/admin/agent-profiles/{profileId}");
            deleted.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task UpsertRejectsMalformedToolPatterns()
    {
        using var admin = fixture.ClientFor("system_admin");
        using var response = await admin.PutAsJsonAsync("/api/admin/agent-profiles", new
        {
            moduleId = "finance",
            name = "bad-patterns",
            instructions = "x",
            mode = "Append",
            isDefault = false,
            toolNames = new[] { "ok_tool", "in*fix" },
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<string> ChatAsync(HttpClient client)
    {
        using var chat = await client.PostAsJsonAsync("/api/agui/finance",
            new { messages = new[] { new { id = "m1", role = "user", content = Question } } });
        chat.EnsureSuccessStatusCode();
        return await chat.Content.ReadAsStringAsync();
    }
}

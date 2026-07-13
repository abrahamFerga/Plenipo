using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Sample.Host.IntegrationTests.Evals;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Module-shipped workflows: Legal's "engage-and-docket" chains drafter → docketing as two full
/// authorized turns in one conversation. Each step keeps its own narrowed tool surface (the
/// drafter can touch clauses but not deadlines; the clerk the reverse), the streamed reply labels
/// the steps, and the run finishes once with the shared conversation id.
/// </summary>
[Collection("api")]
public sealed class ModuleWorkflowTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task WorkflowAppearsInThePickerPayload()
    {
        var modules = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var agents = modules.EnumerateArray().First(m => m.GetProperty("id").GetString() == "legal")
            .GetProperty("agents").EnumerateArray().ToArray();

        var wf = agents.Single(a => a.GetProperty("name").GetString() == "engage-and-docket");
        Assert.StartsWith("Workflow ·", wf.GetProperty("description").GetString());
    }

    [Fact]
    public async Task WorkflowRunsEachStepWithItsOwnToolSurface_InOneConversation()
    {
        using var admin = fixture.ClientFor("system_admin");

        // "clauses" routes the drafter step; "deadline" the docketing step — each token only
        // matches tools inside that step's selection, so a cross-call proves a leak.
        using var chat = await admin.PostAsJsonAsync("/api/agui/legal", new
        {
            messages = new[] { new { id = "m1", role = "user", content = "Search the clauses for confidentiality, and note the deadline for signature." } },
            forwardedProps = new { agent = "engage-and-docket" },
        });
        chat.EnsureSuccessStatusCode();
        var run = EvalRun.Parse(await chat.Content.ReadAsStringAsync());

        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
        // Step 1 (drafter): a clause tool fired; step 2 (docketing): a deadline tool fired.
        Assert.Contains(run.ToolCalls, t => t.Contains("clause", StringComparison.Ordinal));
        Assert.Contains(run.ToolCalls, t => t.Contains("deadline", StringComparison.Ordinal));
        // The reply labels the chain's steps.
        Assert.Contains("**drafter** (1/2)", run.AssistantText);
        Assert.Contains("**docketing** (2/2)", run.AssistantText);
        // One RUN_FINISHED — the workflow completes as a single run.
        Assert.Equal(1, run.EventTypes.Count(t => t == "RUN_FINISHED"));
    }
}

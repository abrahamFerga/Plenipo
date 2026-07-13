using System.Net.Http.Json;

namespace Plenipo.Sample.Host.IntegrationTests.Evals;

/// <summary>
/// Runs every golden-conversation eval under Evals/cases through the real API (full pipeline:
/// auth, RBAC tool filtering, approval gate, audit, Mock provider) and enforces each case's
/// behavioral contract. This is the regression net for prompt-shaped changes: edit a manifest's
/// instructions, a tool description, or ship an agent profile, and these tell you whether agent
/// behavior moved. Add a case by dropping a JSON file — no test code.
/// </summary>
[Collection("api")]
public sealed class GoldenConversationEvals(IntegrationFixture fixture)
{
    public static TheoryData<string> Cases()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Evals", "cases");
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Eval(string caseName)
    {
        var evalCase = EvalCase.Load(Path.Combine(AppContext.BaseDirectory, "Evals", "cases", $"{caseName}.json"));
        using var client = fixture.ClientFor(evalCase.Role);

        using var response = await client.PostAsJsonAsync($"/api/agui/{evalCase.Module}",
            new { messages = new[] { new { id = "m1", role = "user", content = evalCase.Message } } });
        response.EnsureSuccessStatusCode();
        var run = EvalRun.Parse(await response.Content.ReadAsStringAsync());

        // Every turn must complete the protocol without a runtime error.
        Assert.Contains("RUN_STARTED", run.EventTypes);
        Assert.Contains("RUN_FINISHED", run.EventTypes);
        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);

        foreach (var tool in evalCase.ExpectToolCalls)
        {
            Assert.Contains(tool, run.ToolCalls);
        }

        foreach (var tool in evalCase.ForbidToolCalls)
        {
            Assert.DoesNotContain(tool, run.ToolCalls);
        }

        if (evalCase.ExpectApproval)
        {
            Assert.Contains("approval_required", run.CustomEvents);
        }
        else
        {
            Assert.DoesNotContain("approval_required", run.CustomEvents);
        }

        foreach (var expected in evalCase.ReplyMustContain)
        {
            Assert.Contains(expected, run.AssistantText, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var forbidden in evalCase.ReplyMustNotContain)
        {
            Assert.DoesNotContain(forbidden, run.AssistantText, StringComparison.OrdinalIgnoreCase);
        }
    }
}

using Plenipo.Testing.AgUi;
using Xunit;

namespace Plenipo.Testing.Evals;

/// <summary>
/// Runs every golden-conversation eval under <c>Evals/cases</c> through the real API — auth, RBAC
/// tool filtering, the approval gate, audit, the deterministic Mock provider — and enforces each
/// case's behavioural contract against the parsed AG-UI stream. Derive once in the product's test
/// project; add a case by dropping a JSON file.
/// </summary>
/// <example>
/// <code>
/// [Collection("api")]
/// public sealed class Evals(Fixture f) : PlenipoGoldenEvals&lt;Program&gt;(f);
/// </code>
/// </example>
public abstract class PlenipoGoldenEvals<TProgram>(PlenipoHostFixture<TProgram> fixture)
    where TProgram : class
{
    [Fact]
    public void At_least_one_eval_case_is_committed()
    {
        Assert.True(
            EvalCases.Names().Length > 0,
            $"No golden eval cases found under {EvalCases.CasesDirectory}. The contract requires at least a read " +
            "that routes to its tool, a write that requires approval, a narrow role that never sees the write tool, " +
            "and a plain chat turn. See docs/TESTING_CONTRACT.md §5.1.");
    }

    [Theory]
    [MemberData(nameof(EvalCases.All), MemberType = typeof(EvalCases))]
    public async Task Eval(string caseName)
    {
        var evalCase = EvalCases.Load(caseName);
        using var client = fixture.ClientFor(evalCase.Role);

        var run = await client.ChatAsync(evalCase.Module, evalCase.Message);

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

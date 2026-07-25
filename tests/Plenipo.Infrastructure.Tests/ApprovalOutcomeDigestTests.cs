using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Approvals;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// The note the runner prepends when approvals resolved outside the chat: every status renders its
/// decision + attribution, results are relayed (truncated — a tool result can be a whole report),
/// and the framing explicitly supersedes the earlier "NOT executed" tool message.
/// </summary>
public sealed class ApprovalOutcomeDigestTests
{
    private static PendingApproval Approval(ApprovalStatus status, string? result = null, string? error = null, string? by = "Alex") =>
        new()
        {
            ModuleId = "test",
            ToolName = "record",
            Status = status,
            Result = result,
            Error = error,
            ResolvedByDisplay = by,
        };

    [Fact]
    public void Executed_RelaysDecisionAttributionAndResult()
    {
        var digest = ApprovalOutcomeDigest.Compose([Approval(ApprovalStatus.Executed, result: "recorded: x")]);

        Assert.Contains("[Approval outcomes]", digest);
        Assert.Contains("supersedes", digest);
        Assert.Contains("'record': APPROVED by Alex and executed. Result: recorded: x", digest);
    }

    [Fact]
    public void Failed_And_Rejected_RenderTheirOutcomes()
    {
        var digest = ApprovalOutcomeDigest.Compose(
        [
            Approval(ApprovalStatus.Failed, error: "boom"),
            Approval(ApprovalStatus.Rejected, by: null),
        ]);

        Assert.Contains("but the execution FAILED: boom", digest);
        // No resolver display recorded → neutral attribution, never an empty "by ".
        Assert.Contains("REJECTED by an authorized approver. Do not retry", digest);
    }

    [Fact]
    public void LongResults_AreTruncated()
    {
        var digest = ApprovalOutcomeDigest.Compose([Approval(ApprovalStatus.Executed, result: new string('x', 2000))]);

        Assert.Contains("…", digest);
        Assert.True(digest.Length < 1200);
    }
}

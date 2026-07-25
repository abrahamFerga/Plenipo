using System.Text;
using Plenipo.Core.Platform;

namespace Plenipo.Infrastructure.Approvals;

/// <summary>
/// Renders resolved approvals into the note the runner prepends to the next turn's model input.
/// This is the hand-back half of human-in-the-loop: the blocking half told the model the action
/// was "NOT executed, pending approval", and that stale statement lives on in the conversation
/// record — this note is what supersedes it once a human has decided.
/// </summary>
public static class ApprovalOutcomeDigest
{
    /// <summary>Per-item cap on relayed result/error text — tool results can be whole reports.</summary>
    private const int MaxOutcomeLength = 600;

    public static string Compose(IReadOnlyList<PendingApproval> resolved)
    {
        var sb = new StringBuilder(
            "[Approval outcomes] The following actions from this conversation were decided by a human " +
            "OUTSIDE this chat. This supersedes any earlier tool result that said an action was " +
            "pending or not executed — answer from these facts:\n");
        foreach (var approval in resolved)
        {
            var by = approval.ResolvedByDisplay is { Length: > 0 } display ? display : "an authorized approver";
            sb.Append("- '").Append(approval.ToolName).Append("': ").Append(approval.Status switch
            {
                ApprovalStatus.Executed =>
                    $"APPROVED by {by} and executed. Result: {Trim(approval.Result) ?? "(the tool returned no output)"}",
                ApprovalStatus.Failed =>
                    $"APPROVED by {by}, but the execution FAILED: {Trim(approval.Error) ?? "(no error recorded)"}",
                ApprovalStatus.Rejected =>
                    $"REJECTED by {by}. Do not retry it unless the user asks again.",
                _ => approval.Status.ToString(),
            }).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static string? Trim(string? text) =>
        text is null ? null
            : text.Length <= MaxOutcomeLength ? text
            : text[..MaxOutcomeLength] + "…";
}

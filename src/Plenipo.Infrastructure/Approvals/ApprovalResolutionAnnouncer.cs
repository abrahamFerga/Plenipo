using Plenipo.Application.Conversations;
using Plenipo.Application.Notifications;
using Plenipo.Core.Platform;
using Microsoft.Extensions.Logging;

namespace Plenipo.Infrastructure.Approvals;

/// <summary>
/// Closes the feedback loop after a human resolves an approval: the requester should not have to
/// ASK the assistant whether their click did anything. Two announcements, both best-effort:
/// <list type="number">
///   <item>a visible assistant message appended to the originating conversation, so the transcript
///   itself says what happened (the shell also shows it instantly at the click site — this persists
///   it for every later read);</item>
///   <item>an in-app notification to the REQUESTER when someone else decided — approvers already
///   acted, requesters are the ones left wondering.</item>
/// </list>
/// The model's own grounding is separate (the runner's approval-outcome digest); this class is for
/// the humans. Never throws: a feedback hiccup must not fail the approval that already executed.
/// </summary>
public sealed class ApprovalResolutionAnnouncer(
    IConversationStore conversations,
    INotifier notifier,
    ILogger<ApprovalResolutionAnnouncer> logger)
{
    /// <summary>Announces, and returns the composed note so the resolving endpoint can echo it to the click site.</summary>
    public async Task<string> AnnounceAsync(
        PendingApproval approval,
        ApprovalStatus status,
        string? result,
        string? error,
        Guid? resolverUserId,
        string? resolverDisplay,
        CancellationToken cancellationToken = default)
    {
        var note = ComposeNote(approval.ToolName, status, result, error, resolverDisplay);

        try
        {
            await conversations.AppendAssistantNoteAsync(approval.ConversationId, note, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not append the resolution note for approval {ApprovalId} to conversation {ConversationId}.",
                approval.Id, approval.ConversationId);
        }

        // Self-resolved actions need no ping — the resolver watched it happen.
        if (approval.UserId is not { } requester || requester == resolverUserId)
        {
            return note;
        }

        try
        {
            await notifier.NotifyAsync(
                new Notification(
                    approval.TenantId,
                    requester,
                    Category: $"{approval.ModuleId}.approvals",
                    Title: TitleFor(status, approval.ToolName),
                    Body: note,
                    Link: "/chat"),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not notify the requester about resolved approval {ApprovalId}.", approval.Id);
        }

        return note;
    }

    /// <summary>
    /// The transcript wording. The shell composes the SAME shape locally for its instant echo
    /// (PendingApprovals → ChatPanel), so what the user sees at the click and what history replays
    /// later read as one voice.
    /// </summary>
    public static string ComposeNote(
        string toolName, ApprovalStatus status, string? result, string? error, string? resolverDisplay)
    {
        var by = resolverDisplay is { Length: > 0 } display ? display : "an approver";
        return status switch
        {
            ApprovalStatus.Executed =>
                $"✅ Approved by {by} — '{toolName}' ran. {(string.IsNullOrWhiteSpace(result) ? "It returned no output." : result)}",
            ApprovalStatus.Failed =>
                $"⚠️ Approved by {by}, but '{toolName}' failed: {(string.IsNullOrWhiteSpace(error) ? "no error was recorded." : error)}",
            ApprovalStatus.Rejected =>
                $"🚫 Rejected by {by} — '{toolName}' will not run.",
            _ => $"'{toolName}' was resolved as {status}.",
        };
    }

    private static string TitleFor(ApprovalStatus status, string toolName) => status switch
    {
        ApprovalStatus.Executed => $"Approved and ran: {toolName}",
        ApprovalStatus.Failed => $"Approved, but failed: {toolName}",
        ApprovalStatus.Rejected => $"Rejected: {toolName}",
        _ => $"Resolved: {toolName}",
    };
}

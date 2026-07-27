using Plenipo.Core.Platform;

namespace Plenipo.Application.Conversations;

/// <summary>
/// Persistence for chat conversations and their serialized agent sessions. Tenant-scoped by the
/// underlying DbContext's global query filter, so callers can never reach another tenant's threads.
/// </summary>
public interface IConversationStore
{
    public Task<Conversation?> FindAsync(Guid conversationId, CancellationToken cancellationToken = default);

    public Task<Conversation> CreateAsync(string moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the conversation with this id, creating it (owned by the current tenant/user) if it does not
    /// yet exist. For clients that own the thread id — e.g. AG-UI — so reusing an id continues the same
    /// conversation instead of starting a fresh one each turn.
    /// </summary>
    public Task<Conversation> GetOrCreateAsync(Guid conversationId, string moduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist a completed turn: the updated session blob, a derived title, and both messages.
    /// <paramref name="instructionsHash"/> stamps the assistant message with the provenance of the
    /// effective instructions the turn ran under (see <c>InstructionSnapshot</c>).
    /// </summary>
    public Task AppendTurnAsync(
        Guid conversationId,
        string userMessage,
        string assistantMessage,
        string? sessionState,
        string? instructionsHash = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Append a single platform-authored assistant message outside a model turn — e.g. the outcome
    /// of an approval resolved on the approvals surface, written back so the transcript itself says
    /// what happened. Does NOT touch the serialized session: the model learns outcomes through its
    /// own channel (the runner's approval-outcome digest), this is for the human reading the thread.
    /// </summary>
    public Task AppendAssistantNoteAsync(Guid conversationId, string content, CancellationToken cancellationToken = default);
}

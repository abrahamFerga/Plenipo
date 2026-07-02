using Cortex.Core.Entities;
using Cortex.Core.Multitenancy;

namespace Cortex.Core.Platform;

/// <summary>A chat thread between a user and a module's agent. Its messages are persisted and replayed
/// to rebuild the agent's context on the next turn — that is how a conversation resumes across processes.</summary>
public sealed class Conversation : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The module context this conversation runs in.</summary>
    public required string ModuleId { get; set; }

    public string? Title { get; set; }

    /// <summary>
    /// The serialized MAF <c>AgentSession</c> — the framework-owned conversation state (full history
    /// including tool calls/results), written after every turn and resumed on the next. Null for
    /// conversations that haven't had a sessioned turn yet; the runner then seeds a fresh session by
    /// replaying the persisted <see cref="Messages"/> (which remain the display/history source of truth).
    /// </summary>
    public string? SessionState { get; set; }

    public ICollection<ConversationMessage> Messages { get; set; } = [];
}

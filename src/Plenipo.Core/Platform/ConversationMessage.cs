using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

public enum MessageRole
{
    User = 0,
    Assistant = 1,
}

/// <summary>A persisted message in a <see cref="Conversation"/>, kept for display and history.</summary>
public sealed class ConversationMessage : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ConversationId { get; set; }

    public required MessageRole Role { get; set; }
    public required string Content { get; set; }

    /// <summary>
    /// For assistant messages: the hash of the effective instructions the turn ran under
    /// (resolve via <see cref="InstructionSnapshot"/>). Null on user messages and on turns
    /// recorded before provenance existed.
    /// </summary>
    public string? InstructionsHash { get; set; }
}

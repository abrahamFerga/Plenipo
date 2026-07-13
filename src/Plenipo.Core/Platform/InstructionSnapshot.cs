using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>
/// One distinct effective-instruction assembly, stored once per tenant and referenced by hash
/// from every assistant message that ran under it (<see cref="ConversationMessage.InstructionsHash"/>).
/// Append-only in practice: rows are never edited, so a hash recorded on a message years ago
/// still resolves to byte-identical prompt text.
/// </summary>
public sealed class InstructionSnapshot : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Lowercase hex SHA-256 of <see cref="Instructions"/>.</summary>
    public required string Hash { get; set; }

    public required string Instructions { get; set; }
}

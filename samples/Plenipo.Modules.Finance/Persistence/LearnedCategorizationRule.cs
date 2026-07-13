using Plenipo.Core.Entities;

namespace Plenipo.Modules.Finance.Persistence;

/// <summary>
/// A tenant-specific categorization rule learned from a user correction — ported from the-ledger's
/// <c>CategorizationRule</c>. When a description contains <see cref="MatchPattern"/>, the transaction is
/// assigned <see cref="Category"/>. Higher <see cref="Priority"/> wins, and learned rules take
/// precedence over the built-in merchant defaults. This is what lets Finance get smarter over time.
/// </summary>
public sealed class LearnedCategorizationRule : TenantEntityBase
{
    public required string MatchPattern { get; set; }
    public required string Category { get; set; }
    public int Priority { get; set; }
}

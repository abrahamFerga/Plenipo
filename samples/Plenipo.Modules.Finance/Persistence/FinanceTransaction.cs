using Plenipo.Core.Entities;

namespace Plenipo.Modules.Finance.Persistence;

public enum TransactionDirection
{
    /// <summary>Money out (spending).</summary>
    Debit = 0,

    /// <summary>Money in (income / deposits).</summary>
    Credit = 1,
}

/// <summary>
/// A stored transaction in the tenant's ledger — a focused port of the-ledger's <c>Transaction</c>.
/// Amounts are stored as a positive magnitude with a <see cref="Direction"/> and currency; the
/// resolved category and its confidence come from the categorizer at ingestion time.
/// </summary>
public sealed class FinanceTransaction : TenantEntityBase
{
    public DateOnly Date { get; set; }
    public required string Description { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "MXN";
    public TransactionDirection Direction { get; set; }

    public string? Category { get; set; }
    public CategorizationSource CategorizationSource { get; set; } = CategorizationSource.None;
    public double? Confidence { get; set; }
}

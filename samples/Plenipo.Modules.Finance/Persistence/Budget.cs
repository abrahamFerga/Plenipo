using Plenipo.Core.Entities;

namespace Plenipo.Modules.Finance.Persistence;

/// <summary>
/// A monthly spending cap for a category in the tenant's ledger — a focused port of the-ledger's budgets.
/// The <c>check_budget</c> agent tool compares recent spending against these limits.
/// </summary>
public sealed class Budget : TenantEntityBase
{
    public required string Category { get; set; }

    /// <summary>The monthly spending limit for this category.</summary>
    public decimal MonthlyLimit { get; set; }

    public string Currency { get; set; } = "MXN";
}

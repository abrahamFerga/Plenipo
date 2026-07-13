using Plenipo.Core.Multitenancy;
using Plenipo.Modules.Finance;
using Plenipo.Modules.Finance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Modules.Finance.Tests;

/// <summary>
/// Verifies the DB-backed spending summary: it aggregates debits by category over the lookback window,
/// excludes credits (income) and transactions older than the window, and reports the grand total.
/// </summary>
public sealed class FinanceLedgerToolsTests
{
    private sealed class FakeTenant(Guid id) : ITenantContext
    {
        public Guid? TenantId => id;
        public bool HasTenant => true;
        public Guid RequireTenantId() => id;
    }

    private static FinanceDbContext NewContext(Guid tenantId, string store) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(store).Options, new FakeTenant(tenantId));

    [Fact]
    public async Task SummarizeSpending_AggregatesRecentDebitsByCategory()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext(tenant, $"summary-{Guid.NewGuid()}");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        db.Transactions.AddRange(
            new FinanceTransaction { TenantId = tenant, Date = today, Description = "Aldi", Amount = 100m, Direction = TransactionDirection.Debit, Category = "Groceries" },
            new FinanceTransaction { TenantId = tenant, Date = today.AddDays(-3), Description = "Oxxo", Amount = 50m, Direction = TransactionDirection.Debit, Category = "Groceries" },
            new FinanceTransaction { TenantId = tenant, Date = today.AddDays(-2), Description = "Uber", Amount = 30m, Direction = TransactionDirection.Debit, Category = "Transport" },
            new FinanceTransaction { TenantId = tenant, Date = today.AddDays(-60), Description = "Old Purchase", Amount = 999m, Direction = TransactionDirection.Debit, Category = "Shopping" },
            new FinanceTransaction { TenantId = tenant, Date = today, Description = "Payroll", Amount = 5000m, Direction = TransactionDirection.Credit, Category = "Income" });
        await db.SaveChangesAsync();

        var result = await new FinanceLedgerTools(db, new FakeTenant(tenant)).SummarizeSpendingAsync(30, CancellationToken.None);

        Assert.Contains("Groceries 150", result, StringComparison.Ordinal);  // 100 + 50
        Assert.Contains("Transport 30", result, StringComparison.Ordinal);
        Assert.Contains("180", result, StringComparison.Ordinal);            // grand total of recent debits
        Assert.DoesNotContain("Shopping", result, StringComparison.Ordinal); // older than the window
        Assert.DoesNotContain("Income", result, StringComparison.Ordinal);   // a credit, not spending
    }

    [Fact]
    public async Task SummarizeSpending_WithNoData_ReportsNothing()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext(tenant, $"empty-{Guid.NewGuid()}");

        var result = await new FinanceLedgerTools(db, new FakeTenant(tenant)).SummarizeSpendingAsync(30, CancellationToken.None);

        Assert.Contains("No spending recorded", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordTransaction_WritesADebit_ForTheTenant()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext(tenant, $"record-{Guid.NewGuid()}");

        var result = await new FinanceLedgerTools(db, new FakeTenant(tenant))
            .RecordTransactionAsync("OXXO groceries", 123.45m, "Groceries", CancellationToken.None);

        var stored = await db.Transactions.SingleAsync();
        Assert.Equal(tenant, stored.TenantId);
        Assert.Equal("OXXO groceries", stored.Description);
        Assert.Equal(123.45m, stored.Amount);
        Assert.Equal(TransactionDirection.Debit, stored.Direction);
        Assert.Equal("Groceries", stored.Category);
        Assert.Contains("Recorded", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordTransaction_NegativeAmount_IsACredit()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext(tenant, $"record-credit-{Guid.NewGuid()}");

        await new FinanceLedgerTools(db, new FakeTenant(tenant))
            .RecordTransactionAsync("Payroll", -5000m, null, CancellationToken.None);

        var stored = await db.Transactions.SingleAsync();
        Assert.Equal(TransactionDirection.Credit, stored.Direction);
        Assert.Equal(5000m, stored.Amount); // stored as positive magnitude
    }

    [Fact]
    public void Manifest_MarksRecordTransaction_AsRequiringApproval()
    {
        var tool = new FinanceModule().Manifest.Tools.Single(t => t.Name == "record_transaction");
        Assert.True(tool.RequiresApproval);
    }
}

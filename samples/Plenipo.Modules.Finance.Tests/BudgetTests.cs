using Plenipo.Core.Multitenancy;
using Plenipo.Modules.Finance;
using Plenipo.Modules.Finance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Modules.Finance.Tests;

/// <summary>Verifies the <c>check_budget</c> tool: spending-vs-limit math, over/under status, and tenant scoping.</summary>
public sealed class BudgetTests
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
    public async Task CheckBudget_ReportsOverAndUnder()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext(tenant, $"budget-{Guid.NewGuid()}");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        db.Budgets.AddRange(
            new Budget { TenantId = tenant, Category = "Groceries", MonthlyLimit = 100m },
            new Budget { TenantId = tenant, Category = "Transport", MonthlyLimit = 200m });
        db.Transactions.AddRange(
            new FinanceTransaction { TenantId = tenant, Date = today, Description = "Aldi", Amount = 120m, Direction = TransactionDirection.Debit, Category = "Groceries" },
            new FinanceTransaction { TenantId = tenant, Date = today, Description = "Uber", Amount = 50m, Direction = TransactionDirection.Debit, Category = "Transport" });
        await db.SaveChangesAsync();

        var result = await new FinanceLedgerTools(db, new FakeTenant(tenant)).CheckBudgetAsync(null, CancellationToken.None);

        Assert.Contains("Groceries: spent 120 of 100", result, StringComparison.Ordinal);
        Assert.Contains("OVER budget by 20", result, StringComparison.Ordinal);
        Assert.Contains("Transport: spent 50 of 200", result, StringComparison.Ordinal);
        Assert.Contains("150 remaining", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckBudget_SingleCategory_FiltersToIt()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext(tenant, $"budget-one-{Guid.NewGuid()}");
        db.Budgets.AddRange(
            new Budget { TenantId = tenant, Category = "Groceries", MonthlyLimit = 100m },
            new Budget { TenantId = tenant, Category = "Transport", MonthlyLimit = 200m });
        await db.SaveChangesAsync();

        var result = await new FinanceLedgerTools(db, new FakeTenant(tenant)).CheckBudgetAsync("Groceries", CancellationToken.None);

        Assert.Contains("Groceries", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Transport", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckBudget_NoBudgets_SaysSo()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext(tenant, $"budget-none-{Guid.NewGuid()}");

        var result = await new FinanceLedgerTools(db, new FakeTenant(tenant)).CheckBudgetAsync(null, CancellationToken.None);

        Assert.Contains("No budgets", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_DeclaresCheckBudgetTool_NotRequiringApproval()
    {
        var tool = new FinanceModule().Manifest.Tools.Single(t => t.Name == "check_budget");
        Assert.False(tool.RequiresApproval);
    }
}

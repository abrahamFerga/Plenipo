using System.ComponentModel;
using System.Text;
using Plenipo.Core.Multitenancy;
using Plenipo.Modules.Finance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Modules.Finance;

/// <summary>
/// Agent tools over the tenant's stored ledger. Tenant-scoped through <see cref="FinanceDbContext"/>'s
/// global query filter, so the agent can only ever touch the current tenant's transactions. The
/// read tools run freely; the write tool (<see cref="RecordTransactionAsync"/>) is side-effecting and
/// is gated behind human approval by its manifest descriptor.
/// </summary>
public sealed class FinanceLedgerTools(FinanceDbContext db, ITenantContext tenant)
{
    [Description("Summarize the tenant's spending by category over a recent period, computed from stored transactions. " +
                 "Returns the per-category totals (largest first) and the grand total.")]
    public async Task<string> SummarizeSpendingAsync(
        [Description("How many days back to include (default 30).")] int days = 30,
        CancellationToken cancellationToken = default)
    {
        var lookback = days <= 0 ? 30 : days;
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-lookback));

        var totals = await db.Transactions
            .Where(t => t.Direction == TransactionDirection.Debit && t.Date >= since)
            .GroupBy(t => t.Category ?? "Uncategorized")
            .Select(g => new { Category = g.Key, Total = g.Sum(x => x.Amount) })
            .OrderByDescending(x => x.Total)
            .ToListAsync(cancellationToken);

        if (totals.Count == 0)
        {
            return $"No spending recorded in the last {lookback} days.";
        }

        var grandTotal = totals.Sum(x => x.Total);
        var builder = new StringBuilder();
        builder.Append($"Spending over the last {lookback} days totals {grandTotal:0.##}. By category: ");
        builder.AppendJoin(", ", totals.Select(t => $"{t.Category} {t.Total:0.##}"));
        builder.Append('.');
        return builder.ToString();
    }

    [Description("Compare recent spending against the monthly budget for a category, or all budgeted " +
                 "categories. Reports how much is spent, the limit, and whether it is over or under budget.")]
    public async Task<string> CheckBudgetAsync(
        [Description("Category to check (e.g. 'Groceries'), or leave empty to check every budgeted category.")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var budgetsQuery = db.Budgets.AsQueryable();
        if (!string.IsNullOrWhiteSpace(category))
        {
            budgetsQuery = budgetsQuery.Where(b => b.Category == category);
        }

        var budgets = await budgetsQuery.ToListAsync(cancellationToken);
        if (budgets.Count == 0)
        {
            return string.IsNullOrWhiteSpace(category)
                ? "No budgets are set yet."
                : $"No budget is set for {category}.";
        }

        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30));
        var spendByCategory = await db.Transactions
            .Where(t => t.Direction == TransactionDirection.Debit && t.Date >= since && t.Category != null)
            .GroupBy(t => t.Category!)
            .Select(g => new { Category = g.Key, Spent = g.Sum(x => x.Amount) })
            .ToListAsync(cancellationToken);
        var spent = spendByCategory.ToDictionary(x => x.Category, x => x.Spent, StringComparer.OrdinalIgnoreCase);

        var report = new StringBuilder();
        foreach (var budget in budgets.OrderBy(b => b.Category, StringComparer.Ordinal))
        {
            var used = spent.TryGetValue(budget.Category, out var s) ? s : 0m;
            var remaining = budget.MonthlyLimit - used;
            var status = remaining < 0 ? $"OVER budget by {-remaining:0.##}" : $"{remaining:0.##} remaining";
            report.Append($"{budget.Category}: spent {used:0.##} of {budget.MonthlyLimit:0.##} {budget.Currency} ({status}). ");
        }

        return report.ToString().TrimEnd();
    }

    [Description("Record a NEW transaction in the tenant's ledger. This writes data and is a side-effecting " +
                 "action that requires the user's approval before it takes effect.")]
    public async Task<string> RecordTransactionAsync(
        [Description("What the transaction was for, e.g. 'OXXO groceries'.")] string description,
        [Description("Amount. Positive is spending (debit); negative is income (credit).")] decimal amount,
        [Description("Optional spending category, e.g. 'Groceries'.")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var transaction = new FinanceTransaction
        {
            TenantId = tenant.RequireTenantId(),
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            Description = description,
            Amount = Math.Abs(amount),
            Currency = "MXN",
            Direction = amount < 0 ? TransactionDirection.Credit : TransactionDirection.Debit,
            Category = category,
            CategorizationSource = category is null ? CategorizationSource.None : CategorizationSource.Manual,
            Confidence = category is null ? null : 1.0d,
        };

        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(cancellationToken);

        return $"Recorded a {transaction.Direction} of {transaction.Amount:0.##} {transaction.Currency} for '{description}'"
            + (category is null ? "." : $" categorized as {category}.");
    }
}

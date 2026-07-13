using Plenipo.Core.Multitenancy;
using Plenipo.Modules.Finance;
using Plenipo.Modules.Finance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Modules.Finance.Tests;

/// <summary>
/// Verifies the tenant-aware categorizer behaves like the-ledger's CompositeCategorizer: learned rules
/// win over built-in rules, higher priority wins among learned rules, and another tenant's learned
/// rules are invisible (the module's global query filter enforces isolation).
/// </summary>
public sealed class FinanceCategorizerTests
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
    public async Task LearnedRule_OverridesBuiltInRule()
    {
        var tenant = Guid.NewGuid();
        var store = $"override-{Guid.NewGuid()}";
        await using var db = NewContext(tenant, store);
        db.CategorizationRules.Add(new LearnedCategorizationRule
        {
            TenantId = tenant,
            MatchPattern = "OXXO",
            Category = "Business Meals",
            Priority = 100,
        });
        await db.SaveChangesAsync();

        var result = await new FinanceCategorizer(db).CategorizeTransactionAsync("OXXO TIENDA 55", 80m, CancellationToken.None);

        // Built-in rules would say "Groceries"; the learned rule must win.
        Assert.Contains("Business Meals", result, StringComparison.Ordinal);
        Assert.Contains("learned rule", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmongLearnedRules_HigherPriorityWins()
    {
        var tenant = Guid.NewGuid();
        var store = $"priority-{Guid.NewGuid()}";
        await using var db = NewContext(tenant, store);
        db.CategorizationRules.Add(new LearnedCategorizationRule { TenantId = tenant, MatchPattern = "PAYMENT", Category = "Low", Priority = 10 });
        db.CategorizationRules.Add(new LearnedCategorizationRule { TenantId = tenant, MatchPattern = "PAYMENT", Category = "High", Priority = 99 });
        await db.SaveChangesAsync();

        var result = await new FinanceCategorizer(db).CategorizeTransactionAsync("MONTHLY PAYMENT", 200m, CancellationToken.None);

        Assert.Contains("High", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Low", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoLearnedRule_FallsBackToBuiltInRule()
    {
        var tenant = Guid.NewGuid();
        var store = $"fallback-{Guid.NewGuid()}";
        await using var db = NewContext(tenant, store);

        var result = await new FinanceCategorizer(db).CategorizeTransactionAsync("OXXO TIENDA 55", 80m, CancellationToken.None);

        Assert.Contains("Groceries", result, StringComparison.Ordinal);
        Assert.Contains("built-in", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrectingAnUnknownMerchant_LearnsARuleThatCategorizesFutureTransactions()
    {
        var tenant = Guid.NewGuid();
        await using var db = NewContext(tenant, $"learn-loop-{Guid.NewGuid()}");
        var categorizer = new FinanceCategorizer(db);

        const string unknown = "TLAQUEPASTA LOCAL 9";

        // Initially the merchant is unknown — no deterministic rule matches.
        var before = await categorizer.ResolveAsync(unknown, CancellationToken.None);
        Assert.Equal(CategorizationSource.None, before.Source);

        // Simulate the recategorize endpoint's learn step: derive a pattern and store a learned rule.
        var pattern = FinanceCategorization.DeriveMatchPattern(unknown);
        Assert.Equal("TLAQUEPASTA", pattern);
        db.CategorizationRules.Add(new LearnedCategorizationRule { TenantId = tenant, MatchPattern = pattern!, Category = "Dining", Priority = 100 });
        await db.SaveChangesAsync();

        // A new transaction from the same merchant now auto-categorizes from the learned rule.
        var after = await categorizer.ResolveAsync("TLAQUEPASTA LOCAL 12", CancellationToken.None);
        Assert.Equal(CategorizationSource.LearnedRule, after.Source);
        Assert.Equal("Dining", after.Category);
    }

    [Fact]
    public async Task AnotherTenantsLearnedRule_IsNotVisible()
    {
        var store = $"isolation-{Guid.NewGuid()}";
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Tenant B teaches a rule for OXXO.
        await using (var dbB = NewContext(tenantB, store))
        {
            dbB.CategorizationRules.Add(new LearnedCategorizationRule { TenantId = tenantB, MatchPattern = "OXXO", Category = "Business Meals", Priority = 100 });
            await dbB.SaveChangesAsync();
        }

        // Tenant A must not see it — falls back to the built-in Groceries rule.
        await using var dbA = NewContext(tenantA, store);
        var result = await new FinanceCategorizer(dbA).CategorizeTransactionAsync("OXXO TIENDA 55", 80m, CancellationToken.None);

        Assert.Contains("Groceries", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Business Meals", result, StringComparison.Ordinal);
    }
}

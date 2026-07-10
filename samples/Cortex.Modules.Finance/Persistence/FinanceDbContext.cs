using Cortex.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Finance.Persistence;

/// <summary>
/// The Finance module's own database — it co-locates in the platform database under a dedicated
/// <c>finance</c> schema and migrates itself via the module's <c>MigrateAsync</c> hook. Tenant
/// isolation is enforced by the same global query filter pattern the platform uses, so module data
/// can never leak across tenants either.
/// </summary>
public sealed class FinanceDbContext(
    DbContextOptions<FinanceDbContext> options,
    ITenantContext tenantContext) : Cortex.Modules.Sdk.ModuleDbContext(options)
{
    /// <summary>Connection shared with the platform database (separate schema).</summary>
    public const string ConnectionName = "cortex-platform";
    public const string Schema = "finance";

    public DbSet<LearnedCategorizationRule> CategorizationRules => Set<LearnedCategorizationRule>();
    public DbSet<FinanceTransaction> Transactions => Set<FinanceTransaction>();
    public DbSet<Budget> Budgets => Set<Budget>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<LearnedCategorizationRule>(b =>
        {
            b.ToTable("categorization_rules");
            b.HasKey(x => x.Id);
            b.Property(x => x.MatchPattern).HasMaxLength(200).IsRequired();
            b.Property(x => x.Category).HasMaxLength(64).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Priority });
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<FinanceTransaction>(b =>
        {
            b.ToTable("transactions");
            b.HasKey(x => x.Id);
            b.Property(x => x.Description).HasMaxLength(500).IsRequired();
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.Property(x => x.Direction).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.Category).HasMaxLength(64);
            b.Property(x => x.CategorizationSource).HasConversion<string>().HasMaxLength(32);
            b.HasIndex(x => new { x.TenantId, x.Date });
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Budget>(b =>
        {
            b.ToTable("budgets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Category).HasMaxLength(64).IsRequired();
            b.Property(x => x.MonthlyLimit).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Category }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        base.OnModelCreating(modelBuilder);
    }
}

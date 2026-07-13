using Plenipo.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Modules.Nutrition.Persistence;

/// <summary>
/// The Nutrition module's own database — co-located in the platform database under a dedicated
/// <c>nutrition</c> schema and migrated via the module's <c>MigrateAsync</c> hook. The same global
/// query-filter pattern the platform uses enforces tenant isolation, so diary rows never leak across
/// tenants.
/// </summary>
public sealed class NutritionDbContext(
    DbContextOptions<NutritionDbContext> options,
    ITenantContext tenantContext) : Plenipo.Modules.Sdk.ModuleDbContext(options)
{
    /// <summary>Connection shared with the platform database (separate schema).</summary>
    public const string ConnectionName = "plenipo-platform";
    public const string Schema = "nutrition";

    public DbSet<DiaryEntry> DiaryEntries => Set<DiaryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<DiaryEntry>(b =>
        {
            b.ToTable("diary_entries");
            b.HasKey(x => x.Id);
            b.Property(x => x.FoodName).HasMaxLength(200).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Date });
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        base.OnModelCreating(modelBuilder);
    }
}

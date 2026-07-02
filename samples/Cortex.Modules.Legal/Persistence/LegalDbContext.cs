using Cortex.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// The Legal module's own database — co-located in the platform database under a dedicated
/// <c>legal</c> schema and migrated via the module's <c>MigrateAsync</c> hook. The same global
/// query-filter pattern the platform uses enforces tenant isolation on matters and their documents.
/// </summary>
public sealed class LegalDbContext(
    DbContextOptions<LegalDbContext> options,
    ITenantContext tenantContext) : DbContext(options)
{
    /// <summary>Connection shared with the platform database (separate schema).</summary>
    public const string ConnectionName = "cortex-platform";
    public const string Schema = "legal";

    public DbSet<Matter> Matters => Set<Matter>();
    public DbSet<MatterDocument> MatterDocuments => Set<MatterDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Matter>(b =>
        {
            b.ToTable("matters");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.ClientName).HasMaxLength(200);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasMany(x => x.Documents).WithOne().HasForeignKey(d => d.MatterId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<MatterDocument>(b =>
        {
            b.ToTable("matter_documents");
            b.HasKey(x => x.Id);
            b.Property(x => x.FileName).HasMaxLength(300).IsRequired();
            b.Property(x => x.Note).HasMaxLength(500);
            b.HasIndex(x => x.MatterId);
            b.HasIndex(x => new { x.MatterId, x.FileId }).IsUnique(); // a file attaches to a matter once
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        base.OnModelCreating(modelBuilder);
    }
}

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
    public DbSet<MatterDeadline> MatterDeadlines => Set<MatterDeadline>();
    public DbSet<MatterParty> MatterParties => Set<MatterParty>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<TenantClause> Clauses => Set<TenantClause>();
    public DbSet<PlaybookRule> PlaybookRules => Set<PlaybookRule>();

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

        modelBuilder.Entity<MatterDeadline>(b =>
        {
            b.ToTable("matter_deadlines");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.Notes).HasMaxLength(1000);
            b.HasIndex(x => x.MatterId);
            // The reminder scanner's shape: open, un-reminded deadlines by due date, across tenants.
            b.HasIndex(x => x.DueAt).HasFilter("\"CompletedAt\" IS NULL AND \"ReminderSentAt\" IS NULL");
            b.HasOne<Matter>().WithMany().HasForeignKey(x => x.MatterId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<MatterParty>(b =>
        {
            b.ToTable("matter_parties");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(300).IsRequired();
            b.Property(x => x.Notes).HasMaxLength(500);
            b.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(x => x.MatterId);
            b.HasIndex(x => x.TenantId); // the conflicts check scans the whole tenant's parties
            b.HasOne<Matter>().WithMany().HasForeignKey(x => x.MatterId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<TimeEntry>(b =>
        {
            b.ToTable("time_entries");
            b.HasKey(x => x.Id);
            b.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            b.Property(x => x.UserDisplay).HasMaxLength(200);
            b.Property(x => x.Hours).HasPrecision(5, 2);
            b.HasIndex(x => x.MatterId);
            b.HasIndex(x => new { x.TenantId, x.WorkedOn });
            b.HasOne<Matter>().WithMany().HasForeignKey(x => x.MatterId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<TenantClause>(b =>
        {
            b.ToTable("clauses");
            b.HasKey(x => x.Id);
            b.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.Category).HasMaxLength(100).IsRequired();
            b.Property(x => x.Summary).HasMaxLength(500).IsRequired();
            b.Property(x => x.Template).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<PlaybookRule>(b =>
        {
            b.ToTable("playbook_rules");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.Guidance).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(x => x.TenantId);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        base.OnModelCreating(modelBuilder);
    }
}

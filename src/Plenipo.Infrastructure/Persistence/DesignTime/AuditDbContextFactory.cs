using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plenipo.Infrastructure.Persistence.DesignTime;

/// <summary>Design-time factory for the audit context (placeholder connection — schema only).</summary>
public sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql("Host=localhost;Database=plenipo_audit;Username=postgres;Password=postgres")
            .Options;

        return new AuditDbContext(options);
    }
}

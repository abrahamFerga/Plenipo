using Plenipo.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plenipo.Infrastructure.Persistence.DesignTime;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can construct the context without the Aspire host.
/// The connection string is a placeholder — migrations only need the model shape, not a live database —
/// and a null tenant context satisfies the global query filter (which doesn't affect schema).
/// </summary>
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=localhost;Database=plenipo_platform;Username=postgres;Password=postgres")
            .Options;

        return new PlatformDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool HasTenant => false;
        public Guid RequireTenantId() => throw new InvalidOperationException("No tenant at design time.");
    }
}

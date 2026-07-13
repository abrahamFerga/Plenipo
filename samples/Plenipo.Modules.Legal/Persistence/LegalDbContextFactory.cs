using Plenipo.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plenipo.Modules.Legal.Persistence;

/// <summary>Design-time factory so <c>dotnet ef</c> can build the model without the host (schema only).</summary>
public sealed class LegalDbContextFactory : IDesignTimeDbContextFactory<LegalDbContext>
{
    public LegalDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LegalDbContext>()
            .UseNpgsql("Host=localhost;Database=plenipo_platform;Username=postgres;Password=postgres")
            .Options;

        return new LegalDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool HasTenant => false;
        public Guid RequireTenantId() => throw new InvalidOperationException("No tenant at design time.");
    }
}

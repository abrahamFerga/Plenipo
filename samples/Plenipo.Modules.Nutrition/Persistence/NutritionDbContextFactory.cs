using Plenipo.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plenipo.Modules.Nutrition.Persistence;

/// <summary>Design-time factory so <c>dotnet ef</c> can build the model without the host (schema only).</summary>
public sealed class NutritionDbContextFactory : IDesignTimeDbContextFactory<NutritionDbContext>
{
    public NutritionDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NutritionDbContext>()
            .UseNpgsql("Host=localhost;Database=plenipo_platform;Username=postgres;Password=postgres")
            .Options;

        return new NutritionDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool HasTenant => false;
        public Guid RequireTenantId() => throw new InvalidOperationException("No tenant at design time.");
    }
}

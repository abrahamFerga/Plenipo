using Plenipo.Application.Authorization;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Plenipo.AspNetCore.Setup;

/// <summary>
/// Applies EF Core migrations to the platform and audit databases at startup and seeds a development
/// tenant with all currently installed modules enabled.
/// </summary>
public static class DatabaseInitializer
{
    public const string DevTenantSlug = "dev";

    public static async Task InitializeAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;

        var audit = services.GetRequiredService<AuditDbContext>();
        var platform = services.GetRequiredService<PlatformDbContext>();

        try
        {
            await MigrateOrCreateAsync(audit.Database, cancellationToken);
            await MigrateOrCreateAsync(platform.Database, cancellationToken);
        }
        catch (Exception ex) when (IsDatabaseUnreachable(ex))
        {
            // The #1 first-run mistake is starting the app before its database. Surface a clear, actionable
            // message (as the top-level startup exception) instead of a raw Npgsql socket stack trace.
            var target = DescribeConnectionTarget(platform.Database.GetConnectionString());
            throw new InvalidOperationException(
                $"Plenipo could not reach PostgreSQL at {target}. Is the database running? " +
                "Start it with `docker compose up -d` (or run the Aspire AppHost), then start the app again. " +
                "See GETTING_STARTED.md.", ex);
        }

        if (app.Environment.IsDevelopment())
        {
            await SeedDevTenantAsync(platform, services, cancellationToken);
        }
    }

    /// <summary>
    /// Brings a context's store up to date: applies EF migrations for a relational provider (PostgreSQL in
    /// production), or creates the store from the model for a non-relational provider (the EF in-memory
    /// provider used by endpoint tests, which has no migrations). Production behaviour is unchanged.
    /// </summary>
    private static Task MigrateOrCreateAsync(DatabaseFacade database, CancellationToken cancellationToken) =>
        database.IsRelational()
            ? database.MigrateAsync(cancellationToken)
            : database.EnsureCreatedAsync(cancellationToken);

    /// <summary>True when the exception chain indicates the database server was unreachable (e.g. not started).</summary>
    private static bool IsDatabaseUnreachable(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException or TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Extracts a friendly "host:port" from a connection string, never echoing credentials.</summary>
    private static string DescribeConnectionTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return "the configured PostgreSQL server";
        }

        string? host = null;
        var port = "5432";
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            var key = pair[0].Trim();
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Server", StringComparison.OrdinalIgnoreCase))
            {
                host = pair[1].Trim();
            }
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase))
            {
                port = pair[1].Trim();
            }
        }

        return host is null ? "the configured PostgreSQL server" : $"{host}:{port}";
    }

    private static async Task SeedDevTenantAsync(
        PlatformDbContext platform,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // Tenants are not tenant-owned, so no query filter applies here.
        var tenant = await platform.Tenants.FirstOrDefaultAsync(t => t.Slug == DevTenantSlug, cancellationToken);
        if (tenant is null)
        {
            tenant = new Tenant { Name = "Development Tenant", Slug = DevTenantSlug };
            platform.Tenants.Add(tenant);
            await platform.SaveChangesAsync(cancellationToken);
        }

        // Enable all installed modules for the dev tenant automatically. IgnoreQueryFilters is essential:
        // TenantModule is tenant-owned, but no ambient tenant is set during startup initialization, so the
        // default filter would hide the existing rows — the existence check would always miss and re-insert,
        // crashing a restart against an already-seeded database (duplicate key on TenantId+ModuleId).
        var modules = services.GetServices<IModule>();
        foreach (var module in modules)
        {
            if (!await platform.TenantModules.IgnoreQueryFilters().AnyAsync(
                    tm => tm.TenantId == tenant.Id && tm.ModuleId == module.Manifest.Id,
                    cancellationToken))
            {
                platform.TenantModules.Add(new TenantModule
                {
                    TenantId = tenant.Id,
                    ModuleId = module.Manifest.Id,
                    IsEnabled = true,
                });
            }
        }

        await platform.SaveChangesAsync(cancellationToken);

        await EnsureRolePermissionsSeededAsync(platform, tenant.Id, RoleBaseline.Merge(services.GetServices<ProductRole>()), cancellationToken);
    }

    /// <summary>
    /// Materializes the built-in role → permission defaults as editable rows for a tenant, but only when
    /// the tenant has none yet. This is what makes the baseline editable in the admin console while keeping
    /// existing behaviour: once seeded, the rows are authoritative; a tenant with no rows falls back to the
    /// code defaults. Idempotent and safe to call on every startup. Call before applying a role edit so a
    /// first edit can't strand the other roles at empty.
    /// </summary>
    public static async Task EnsureRolePermissionsSeededAsync(
        PlatformDbContext platform,
        Guid tenantId,
        IReadOnlyDictionary<string, string[]> baseline,
        CancellationToken cancellationToken = default)
    {
        // RolePermission is tenant-owned, but no ambient tenant is set during startup initialization, so
        // bypass the query filter and scope explicitly by tenantId (mirrors the TenantModule seeding above).
        var alreadySeeded = await platform.RolePermissions.IgnoreQueryFilters()
            .AnyAsync(rp => rp.TenantId == tenantId, cancellationToken);
        if (alreadySeeded)
        {
            return;
        }

        foreach (var (role, defaults) in baseline)
        {
            foreach (var permission in defaults)
            {
                platform.RolePermissions.Add(new RolePermission
                {
                    TenantId = tenantId,
                    Role = role,
                    Permission = permission,
                });
            }
        }

        await platform.SaveChangesAsync(cancellationToken);
    }
}

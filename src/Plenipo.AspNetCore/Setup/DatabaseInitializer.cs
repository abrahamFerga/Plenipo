using Plenipo.Application.Authorization;
using Plenipo.Application.Bootstrap;
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

        // Bring any tenant seeded under the old full-set role storage onto the deviation model. Runs
        // before anything reads a permission, is lossless, and stamps itself done per tenant.
        await RoleStorageConversion.ConvertAsync(
            platform,
            RoleBaseline.Merge(services.GetServices<ProductRole>()),
            services.GetRequiredService<ILogger<DatabaseInitializerLog>>(),
            cancellationToken);

        if (app.Environment.IsDevelopment())
        {
            await SeedDevTenantAsync(platform, services, cancellationToken);
        }

        // Local auth mode (ADR 0003): load-or-generate the issuer's keys and upsert the built-in
        // client BEFORE anything serves traffic — and before bootstrap, which creates the first
        // admin's credential.
        if (services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Auth.AuthOptions>>().Value.IsLocalMode)
        {
            await Auth.Local.LocalAuthInitializer.InitializeAsync(services, cancellationToken);
        }

        // Outside Development nothing above creates a tenant, and permissions are only resolved after a
        // tenant resolves — so an empty deployment has nobody who could create one. The Bootstrap section
        // breaks that deadlock once, from configuration, and is inert as soon as an operator exists.
        await services.GetRequiredService<IPlatformBootstrapper>().BootstrapAsync(cancellationToken);
    }

    /// <summary>Log category for startup database work — this class is static, so it cannot be one itself.</summary>
    internal sealed class DatabaseInitializerLog;

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

        // No role rows are written. Under deviation storage a tenant with no rows simply tracks the
        // declared baseline, which is what a fresh tenant should do — and what makes a later
        // AddPlenipoRole change reach it without a reconciler.
    }
}

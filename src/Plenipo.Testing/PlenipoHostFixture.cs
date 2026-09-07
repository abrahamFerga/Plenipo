using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Context;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Plenipo.Testing;

/// <summary>
/// The real product host on a throwaway Postgres: platform and module migrations run, the dev
/// tenant and seed data land, hosted services start. Everything is real except the AI provider
/// (Mock) — the same keyless posture the platform's own suite uses, so the whole security pipeline
/// is exercisable with no secrets.
/// </summary>
/// <remarks>
/// Derive once per product, supply the <see cref="Contract"/>, and register the collection:
/// <code>
/// public sealed class Fixture : PlenipoHostFixture&lt;Program&gt;
/// {
///     public override ProductContract Contract { get; } = new(ModuleId: "finance",
///         ReadTool: "summarize_spending", WriteTool: "record_transaction");
/// }
///
/// [CollectionDefinition("api")]
/// public sealed class ApiCollection : ICollectionFixture&lt;Fixture&gt;;
/// </code>
/// </remarks>
public abstract class PlenipoHostFixture<TProgram> : IAsyncLifetime
    where TProgram : class
{
    private PostgreSqlContainer? _postgres;

    /// <summary>The booted host. Derive configured variants with <see cref="Derive"/>.</summary>
    public WebApplicationFactory<TProgram> Factory { get; private set; } = default!;

    /// <summary>What the kit needs to know about this product. Read from the repo, never invented.</summary>
    public abstract ProductContract Contract { get; }

    /// <summary>
    /// The Postgres image. Must be pgvector, never stock postgres — the platform's RAG migration
    /// creates a vector column at startup and fails hard without the extension.
    /// </summary>
    protected virtual string PostgresImage => "pgvector/pgvector:pg16";

    /// <summary>The ASP.NET environment the host boots in. Development enables the dev-auth headers.</summary>
    protected virtual string HostEnvironment => "Development";

    /// <summary>The container's connection string, for tests that need a second host over the same data.</summary>
    public string ConnectionString =>
        _postgres?.GetConnectionString() ?? throw new InvalidOperationException("The fixture has not started.");

    /// <summary>
    /// Extra host configuration: settings, and test-service replacements via
    /// <c>builder.ConfigureTestServices</c>. <paramref name="factory"/> is the factory being built,
    /// so a lambda may defer to <c>factory.Server.CreateHandler()</c> for loopback HTTP clients.
    /// </summary>
    protected virtual void ConfigureWebHost(IWebHostBuilder builder, WebApplicationFactory<TProgram> factory)
    {
    }

    /// <summary>Runs once the host answers <c>/alive</c> and the dev-tenant admin is provisioned.</summary>
    protected virtual Task OnHostStartedAsync() => Task.CompletedTask;

    /// <summary>Runs before the host and the container are disposed — dispose derived factories here.</summary>
    protected virtual Task OnDisposingAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        // Skip the resource-reaper sidecar, which can be flaky on Docker Desktop.
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");

        _postgres = new PostgreSqlBuilder(PostgresImage)
            .WithDatabase("plenipo_platform")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _postgres.StartAsync();

        // Point the host at the container. Environment variables override appsettings.Development.json
        // (which targets the local docker-compose Postgres) — the same mechanism Aspire uses.
        Environment.SetEnvironmentVariable("ConnectionStrings__plenipo-platform", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("ConnectionStrings__plenipo-audit", _postgres.GetConnectionString());

        Factory = new HostFactory(this);

        // The first request boots the host (migrations + seeding); the authenticated call makes the
        // request enricher provision the dev-tenant admin that AuthorizedScopeAsync relies on.
        using var warmup = ClientFor(Contract.ApproverRole);
        using (var alive = await warmup.GetAsync("/alive"))
        {
            alive.EnsureSuccessStatusCode();
        }

        using (var modules = await warmup.GetAsync("/api/platform/modules"))
        {
            modules.EnsureSuccessStatusCode();
        }

        await OnHostStartedAsync();
    }

    public async Task DisposeAsync()
    {
        await OnDisposingAsync();

        Environment.SetEnvironmentVariable("ConnectionStrings__plenipo-platform", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__plenipo-audit", null);

        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    /// <summary>
    /// An HTTP client authenticated through the dev-auth headers as <paramref name="role"/> in
    /// <paramref name="tenant"/> (the dev tenant by default). PREFER THIS over
    /// <see cref="AuthorizedScopeAsync"/>: it goes through the real pipeline, so it is the only way
    /// to prove RBAC, the approval gate, and the AG-UI protocol. Pass a narrower role to assert a 403.
    /// </summary>
    public HttpClient ClientFor(string role, string? tenant = null, string? subject = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        tenant ??= Contract.DevTenant;
        subject ??= string.Equals(tenant, Contract.DevTenant, StringComparison.Ordinal) ? $"it-{role}" : $"it-{role}-{tenant}";

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        return client;
    }

    /// <summary>A client for the given role in an arbitrary tenant, which must exist (<see cref="EnsureTenantAsync"/>).</summary>
    public HttpClient ClientForTenant(string role, string tenant) => ClientFor(role, tenant);

    /// <summary>The dev tenant's operator: <c>system_admin</c> holds <c>*</c>. Reads every admin endpoint.</summary>
    public HttpClient AdminClient(string roles = "system_admin", string subject = "it-admin") =>
        ClientFor(roles, subject: subject);

    /// <summary>Boots a configured variant of the host over the same database (a restart, a channel switched on).</summary>
    public WebApplicationFactory<TProgram> Derive(Action<IWebHostBuilder> configure) => Factory.WithWebHostBuilder(configure);

    /// <summary>
    /// Ensures a tenant with the given slug exists (only the dev tenant is seeded), so cross-tenant
    /// isolation can be exercised. Tenants are not tenant-owned, so no query filter applies.
    /// </summary>
    public async Task EnsureTenantAsync(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        using var scope = Factory.Services.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        if (!await platform.Tenants.AnyAsync(t => t.Slug == slug))
        {
            platform.Tenants.Add(new Tenant { Name = $"{slug} tenant", Slug = slug });
            await platform.SaveChangesAsync();
        }
    }

    /// <summary>
    /// A DI scope with tenant, user and permissions populated — how module tools run AFTER the
    /// platform's auth and approval pipeline has done its part. Deliberately bypasses RBAC and the
    /// approval gate, so it can never prove either works: use <see cref="ClientFor"/> for those.
    /// </summary>
    public async Task<(IServiceScope Scope, Guid TenantId, Guid UserId)> AuthorizedScopeAsync(string? tenant = null)
    {
        tenant ??= Contract.DevTenant;

        var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        var tenantRow = await db.Tenants.FirstAsync(t => t.Slug == tenant);
        context.SetTenant(tenantRow.Id);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.TenantId == tenantRow.Id);
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
        return (scope, tenantRow.Id, user.Id);
    }

    private sealed class HostFactory(PlenipoHostFixture<TProgram> owner) : WebApplicationFactory<TProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(owner.HostEnvironment);
            owner.ConfigureWebHost(builder, this);
        }
    }
}

using Plenipo.Application.Ai;
using Plenipo.Infrastructure.Ai;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Infrastructure.Persistence.Interceptors;
using Plenipo.Modules.Sdk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Plenipo.Api.Tests;

/// <summary>
/// Hosts the bare <c>Plenipo.Api</c> platform in-process for endpoint tests. Runs in the Development environment
/// so the <c>X-Dev-*</c> dev-auth scheme and the dev-tenant + role-permission seeding are active, with all three
/// EF contexts swapped to a shared in-memory store — no PostgreSQL, no Docker. Each factory instance gets a
/// uniquely-named store so test classes are isolated.
/// </summary>
public class PlenipoApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"plenipo-tests-{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("Connectors:OperatorEnabled:local-folder", "true");
        builder.UseSetting("Connectors:LocalFolder:AllowedRoots:0", Path.GetTempPath());
        builder.UseSetting("Security:OutboundUrls:AllowHttp", "true");
        builder.UseSetting("Security:OutboundUrls:AllowPrivateNetworks", "true");

        builder.ConfigureServices(services =>
        {
            // Platform + Outbox share one logical store (the outbox table lives in the platform DB); Audit is
            // its own. Only the platform context carries the audit interceptor.
            ReplaceWithInMemory<PlatformDbContext>(services, _databaseName, withInterceptor: true);
            ReplaceWithInMemory<OutboxDbContext>(services, _databaseName, withInterceptor: false);
            ReplaceWithInMemory<AuditDbContext>(services, $"{_databaseName}-audit", withInterceptor: false);

            // A minimal module so the chat pipeline has a valid module to run against (dev seeding enables it),
            // plus its executable tool source so tool-permission filtering can be exercised end to end.
            services.AddSingleton<IModule>(new TestModule());
            services.AddSingleton<IModuleToolSource>(new TestToolSource());

            // Enable the dependency-free Mock chat client so the agent pipeline runs end to end without an API
            // key. AddAgentStack reads Ai:Provider at registration (before a config override would apply) and
            // only registers IChatClient when enabled, so override the bound options AND supply the client here.
            services.Configure<AiOptions>(options => options.Provider = "Mock");
            services.AddSingleton<IChatClient>(new MockChatClient());
        });
    }

    private static void ReplaceWithInMemory<TContext>(IServiceCollection services, string databaseName, bool withInterceptor)
        where TContext : DbContext
    {
        // Strip EVERY registration parameterized by this context — the context itself, its options, and the
        // Aspire/Npgsql DbContext POOL infrastructure (IDbContextPool<T>, IScopedDbContextLease<T>, factories) —
        // so the in-memory re-registration below is the only one and no leftover singleton pool references the
        // old scoped options.
        var contextType = typeof(TContext);
        var stale = services
            .Where(d => d.ServiceType == contextType
                || (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(contextType)))
            .ToList();
        foreach (var descriptor in stale)
        {
            services.Remove(descriptor);
        }

        services.AddDbContext<TContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(databaseName);
            if (withInterceptor)
            {
                options.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
            }
        });
    }
}

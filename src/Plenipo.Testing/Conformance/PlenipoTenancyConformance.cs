using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Plenipo.Core.Multitenancy;
using Plenipo.Testing.AgUi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Testing.Conformance;

/// <summary>
/// Tenant isolation by construction, proved two ways: every tenant-owned entity in every registered
/// DbContext carries a global query filter (reflection over the model, so a forgotten
/// <c>HasQueryFilter</c> fails here rather than in production), and a second tenant sees nothing on
/// the product's read surfaces and on the audit log — nothing at all on a list; on a surface the
/// product declares seeded per tenant, or on a singleton tab's document, only its own rows,
/// disjoint by id from the first tenant's (#208).
/// </summary>
public abstract class PlenipoTenancyConformance<TProgram>(PlenipoHostFixture<TProgram> fixture)
    where TProgram : class
{
    private const string OtherTenant = "other";

    private static readonly string[] SkippedAssemblyPrefixes =
        ["Microsoft.", "System.", "Npgsql", "OpenIddict", "Aspire.", "Testcontainers", "xunit", "Docker."];

    private ProductContract Contract => fixture.Contract;

    [Fact]
    public void Every_tenant_owned_entity_in_every_registered_DbContext_has_a_query_filter()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var inspected = 0;
        var missing = new List<string>();

        foreach (var contextType in DbContextTypes())
        {
            DbContext? context;
            try
            {
                context = scope.ServiceProvider.GetService(contextType) as DbContext;
            }
            catch (InvalidOperationException)
            {
                continue; // registered without the services it needs in a bare scope: not this test's concern
            }

            if (context is null)
            {
                continue;
            }

            inspected++;
            foreach (var entity in context.Model.GetEntityTypes())
            {
                if (entity.IsOwned() || !typeof(ITenantOwned).IsAssignableFrom(entity.ClrType))
                {
                    continue;
                }

                if (entity.GetDeclaredQueryFilters().Count == 0)
                {
                    missing.Add($"{contextType.Name}.{entity.ClrType.Name}");
                }
            }
        }

        Assert.True(inspected > 0, "No DbContext could be resolved from the host — the scan found nothing to inspect.");
        Assert.True(missing.Count == 0,
            "Tenant-owned entities without a global query filter (every ITenantOwned entity must be filtered by TenantId): " +
            string.Join(", ", missing));
    }

    [Fact]
    public async Task The_audit_log_is_tenant_isolated()
    {
        await fixture.EnsureTenantAsync(OtherTenant);

        // Drive an audited tool call in the dev tenant so its audit log is non-empty.
        using var approver = fixture.ClientFor(Contract.ApproverRole);
        var run = await approver.ChatAsync(Contract.ModuleId, Contract.EffectiveReadPrompt);
        Assert.True(run.Completed, run.RawSse);
        Assert.Contains(Contract.ReadTool, run.ToolCalls);

        using var admin = fixture.AdminClient();
        var devAudit = await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls?take=50");
        Assert.NotEmpty(devAudit.EnumerateArray());

        // An operator of a DIFFERENT tenant must see none of it — the audit store is tenant-isolated
        // like the platform data, even though it lives in a separate database.
        using var other = fixture.ClientFor("system_admin", OtherTenant);
        var otherAudit = await other.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls?take=50");
        Assert.Empty(otherAudit.EnumerateArray());
    }

    [Fact]
    public async Task A_second_tenant_sees_nothing_on_the_read_surfaces()
    {
        await fixture.EnsureTenantAsync(OtherTenant);

        using var admin = fixture.AdminClient();
        var modules = await admin.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var module = Assert.Single(modules.EnumerateArray(),
            m => string.Equals(m.GetProperty("id").GetString(), Contract.ModuleId, StringComparison.Ordinal));
        var tabs = module.GetProperty("tabs").EnumerateArray()
            .Select(t => (
                Endpoint: t.TryGetProperty("dataEndpoint", out var e) ? e.GetString() : null,
                Singleton: t.TryGetProperty("singleton", out var s) && s.ValueKind == JsonValueKind.True))
            .Where(t => !string.IsNullOrWhiteSpace(t.Endpoint))
            .ToArray();
        // A singleton tab's endpoint returns the tenant's own document (settings, a profile) — a
        // per-tenant row by construction of the tab kind, so a fresh tenant legitimately sees one.
        var singletonRoutes = tabs.Where(t => t.Singleton).Select(t => t.Endpoint!).ToHashSet(StringComparer.Ordinal);
        var routes = tabs.Select(t => t.Endpoint!).Concat(Contract.ReadRoutes).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(routes.Length > 0,
            "No read surface to probe: the module declares no data tabs and ProductContract.ReadEndpoints is empty.");

        using var other = fixture.ClientFor("system_admin", OtherTenant);
        foreach (var route in routes)
        {
            using var dev = await admin.GetAsync(new Uri(route, UriKind.Relative));
            Assert.True(dev.IsSuccessStatusCode, $"GET {route} must succeed for the dev tenant (got {(int)dev.StatusCode}).");
            var devBody = await dev.Content.ReadAsStringAsync();

            using var response = await other.GetAsync(new Uri(route, UriKind.Relative));
            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound,
                $"GET {route} as a second tenant returned {(int)response.StatusCode}; expected an empty 200, 204 or 404.");

            if (response.StatusCode != HttpStatusCode.OK)
            {
                continue;
            }

            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            using var json = JsonDocument.Parse(body);
            var seeded = Contract.SeededReadRoutes.Contains(route, StringComparer.Ordinal);
            if (seeded || singletonRoutes.Contains(route))
            {
                using var devJson = JsonDocument.Parse(string.IsNullOrWhiteSpace(devBody) ? "[]" : devBody);
                // A declared seeded surface must carry ids, or its rows cannot be attributed; a singleton
                // document need not (it is the tenant's own by the tab's definition), but if it has one
                // it must not be the first tenant's.
                AssertOnlyOwnRows(route, devJson.RootElement, json.RootElement, requireIds: seeded);
            }
            else
            {
                AssertNoRows(route, json.RootElement);
            }
        }
    }

    private static void AssertNoRows(string route, JsonElement root)
    {
        var rows = TenantRows.Rows(root);
        Assert.True(rows.Count == 0,
            $"GET {route} as a second tenant returned {rows.Count} rows; expected none. If this surface is per-tenant seeded " +
            "reference data — a starter taxonomy every new tenant gets on first read — declare the route in " +
            "ProductContract.SeededReadEndpoints and the pack asserts the rows are the second tenant's own (disjoint by id) instead.");
    }

    // #208: a seeded surface shows a fresh tenant its own rows. The invariant is still "none of the
    // FIRST tenant's rows", decided by id — which is why every row must carry one here.
    private static void AssertOnlyOwnRows(string route, JsonElement dev, JsonElement other, bool requireIds)
    {
        var rows = TenantRows.Rows(other);
        if (rows.Count == 0)
        {
            return;
        }

        if (requireIds)
        {
            var unattributable = rows.Count(row => TenantRows.IdOf(row) is null);
            Assert.True(unattributable == 0,
                $"GET {route} as a second tenant returned {unattributable} of {rows.Count} rows without an 'id', so the pack cannot tell " +
                "whose they are; a route in ProductContract.SeededReadEndpoints must return an id per row.");
        }

        var shared = TenantRows.SharedIds(dev, other);
        Assert.True(shared.Count == 0,
            $"GET {route} as a second tenant returned {shared.Count} of the first tenant's rows (ids {string.Join(", ", shared.Take(5))}); " +
            "a second tenant must see none of the first tenant's rows, seeded surface or not.");
    }

    private static IEnumerable<Type> DbContextTypes() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !SkippedAssemblyPrefixes.Any(p => (a.GetName().Name ?? "").StartsWith(p, StringComparison.Ordinal)))
            .SelectMany(LoadableTypes)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(DbContext).IsAssignableFrom(t))
            .Distinct();

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t is not null)!;
        }
    }
}

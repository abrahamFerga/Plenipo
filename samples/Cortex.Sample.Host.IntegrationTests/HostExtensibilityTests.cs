using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Application.Commerce;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// Host extensibility wave 1 (improvement loop): a product host can act on tenant provisioning
/// (ITenantProvisionedHook — best-effort, post-commit, both trigger paths) and reshape the JIT
/// default role (Auth:DefaultRole) — no forks.
/// </summary>
[Collection("api")]
public sealed class HostExtensibilityTests : IDisposable
{
    private sealed class RecordingHook : ITenantProvisionedHook
    {
        public ConcurrentQueue<TenantProvisionedContext> Calls { get; } = new();

        public Task OnTenantProvisionedAsync(TenantProvisionedContext context, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(context);
            return Task.CompletedTask;
        }
    }

    private sealed class ExplodingHook : ITenantProvisionedHook
    {
        public Task OnTenantProvisionedAsync(TenantProvisionedContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("welcome email exploded");
    }

    private readonly RecordingHook _hook = new();
    private readonly IntegrationFixture _fixture;
    private readonly WebApplicationFactory<Program> _factory;

    public HostExtensibilityTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
        _factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:DefaultRole", "guest");
            b.ConfigureTestServices(services =>
            {
                // Exactly what a product host writes in Program.cs.
                services.AddSingleton(_hook);
                services.AddScoped<ITenantProvisionedHook>(sp => sp.GetRequiredService<RecordingHook>());
                services.AddCortexTenantProvisionedHook<ExplodingHook>();
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ProvisionedHook_Fires_AndAFailingHookNeverRollsBackTheTenant()
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Dev-Subject", "it-system_admin");
        admin.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        admin.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");

        using var created = await admin.PostAsJsonAsync("/api/admin/tenants/provision", new
        {
            name = "Hooked Co",
            slug = "hooked-co",
            adminEmail = "admin@hooked.example",
            modules = new[] { "legal" },
            maxSeats = 3,
            monthlyTokenBudget = 100_000L,
        });

        // The exploding hook logged, the recording hook fired, the tenant exists.
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var call = Assert.Single(_hook.Calls);
        Assert.Equal("hooked-co", call.Slug);
        Assert.Equal("admin@hooked.example", call.AdminEmail);
        Assert.Equal(new[] { "legal" }, call.EnabledModules);
        Assert.Equal(3, call.MaxSeats);
        Assert.Equal(100_000L, call.MonthlyTokenBudget);
    }

    [Fact]
    public async Task JitDefaultRole_IsConfigurable()
    {
        // A brand-new user signs in with NO roles asserted → gets the CONFIGURED default (guest).
        var user = _factory.CreateClient();
        user.DefaultRequestHeaders.Add("X-Dev-Subject", "extensibility-jit-user");
        user.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        // A present-but-role-less header = an unscoped token (absence defaults to system_admin in
        // dev; HttpClient drops empty header values, so "," parses to zero roles).
        user.DefaultRequestHeaders.TryAddWithoutValidation("X-Dev-Roles", ",");
        (await user.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        var admin = _fixture.ClientFor("system_admin");
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var jit = users.EnumerateArray().First(u => u.GetProperty("subject").GetString() == "extensibility-jit-user");
        Assert.Contains("guest", jit.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
        Assert.DoesNotContain("user", jit.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }
}

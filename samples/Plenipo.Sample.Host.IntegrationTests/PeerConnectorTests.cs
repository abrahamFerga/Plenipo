using System.Net.Http.Json;
using Plenipo.Connectors.Peer;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Verticals are separate systems; the plenipo-peer connector is how they talk. This exercises a
/// real system-to-system AG-UI conversation — the host asks "itself" as a stand-in remote
/// deployment (the fixture routes the connector's HttpClient into the TestServer): the peer's
/// full pipeline runs (auth, module enablement, tool gating, Mock agent, audit) and the answer
/// streams back into the local tool result. Default-off like every connector.
/// </summary>
[Collection("api")]
public sealed class PeerConnectorTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Asks_a_peer_plenipo_system_over_agui_and_returns_its_answer()
    {
        using var admin = fixture.ClientFor("system_admin");

        // Before enablement the tool answers honestly.
        using (var scope = await UserScopeAsync())
        {
            var tools = scope.ServiceProvider.GetRequiredService<PlenipoPeerTools>();
            Assert.Contains("not enabled", await tools.AskPeerSystem("anything"));
        }

        (await admin.PutAsJsonAsync("/api/admin/connectors/plenipo-peer/settings", new
        {
            values = new Dictionary<string, string?>
            {
                ["BaseUrl"] = "http://localhost",
                ["ModuleId"] = "finance",
                ["PeerName"] = "the finance system",
            },
        })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/plenipo-peer/enable", null)).EnsureSuccessStatusCode();

        try
        {
            using var scope = await UserScopeAsync();
            var tools = scope.ServiceProvider.GetRequiredService<PlenipoPeerTools>();

            var answer = await tools.AskPeerSystem("How much did I spend on groceries this month?");

            // The peer's agent (Mock provider, finance module) produced a real streamed reply.
            Assert.StartsWith("Answer from the finance system:", answer);
            Assert.True(answer.Length > "Answer from the finance system:".Length + 10, $"unexpectedly short answer: {answer}");
            Assert.DoesNotContain("declined the request", answer);
            Assert.DoesNotContain("reported an error", answer);
        }
        finally
        {
            (await admin.PostAsync("/api/admin/connectors/plenipo-peer/disable", null)).EnsureSuccessStatusCode();
        }
    }

    /// <summary>A scope acting as the JIT-provisioned dev user with wildcard permissions.</summary>
    private async Task<IServiceScope> UserScopeAsync()
    {
        using (var warmup = fixture.ClientFor("user"))
        {
            (await warmup.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();
        }

        var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();

        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == "it-user");
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
        return scope;
    }
}

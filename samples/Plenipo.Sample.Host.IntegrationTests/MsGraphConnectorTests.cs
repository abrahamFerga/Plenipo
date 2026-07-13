using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Plenipo.Application.Files;
using Plenipo.Connectors.MsGraph;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The delegated-OAuth lane end to end (plan phase 4), keyless via a fake IdP + fake Graph: the
/// admin enables + configures msgraph (stage 1), a USER connects their own account through the
/// real start→callback flow (PKCE state, protected token storage — stage 2), the tools ride that
/// user's token, another user stays unconnected, and DISABLING THE CONNECTOR REVOKES every
/// session — re-enable forces re-auth.
/// </summary>
[Collection("api")]
public sealed class MsGraphConnectorTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task User_connects_via_oauth_tools_ride_their_token_and_disable_revokes()
    {
        using var admin = fixture.ClientFor("system_admin");
        (await admin.PutAsJsonAsync("/api/admin/connectors/msgraph/settings", new
        {
            values = new Dictionary<string, string?>
            {
                ["Authority"] = "https://login.fake-idp.test/tenant-id",
                ["ClientId"] = "client-abc",
                ["Scopes"] = "offline_access Files.Read.All",
            },
        })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/msgraph/enable", null)).EnsureSuccessStatusCode();

        try
        {
            // Stage 2, as a specific user: not connected yet → the tool points at the connect flow.
            using (var scope = await UserScopeAsync("it-user"))
            {
                var tools = scope.ServiceProvider.GetRequiredService<MsGraphTools>();
                Assert.Contains("/api/connectors/msgraph/oauth/start", await tools.ListM365Files());
            }

            // The REAL start endpoint: PKCE + protected state, pointing at the configured IdP.
            using var user = fixture.ClientFor("user");
            var start = await user.GetFromJsonAsync<JsonElement>("/api/connectors/msgraph/oauth/start");
            var authorizeUrl = start.GetProperty("authorizeUrl").GetString()!;
            Assert.StartsWith("https://login.fake-idp.test/tenant-id/oauth2/v2.0/authorize", authorizeUrl);
            Assert.Contains("client_id=client-abc", authorizeUrl);
            Assert.Contains("code_challenge=", authorizeUrl);

            // The REAL callback (fake IdP exchanges the code): tokens land protected on the user.
            var state = HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query)["state"]!;
            var callback = await user.GetAsync(
                $"/api/connectors/msgraph/oauth/callback?code=abc123&state={Uri.EscapeDataString(state)}");
            callback.EnsureSuccessStatusCode();
            Assert.Contains("Connected", await callback.Content.ReadAsStringAsync());

            using (var scope = await UserScopeAsync("it-user"))
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var login = await db.UserConnectorLogins.SingleAsync(l => l.ConnectorId == "msgraph");
                Assert.DoesNotContain("fake-access", login.ProtectedTokensJson); // protected at rest

                // The tools now ride the user's delegated token against (fake) Graph.
                var tools = scope.ServiceProvider.GetRequiredService<MsGraphTools>();
                var listing = await tools.ListM365Files();
                Assert.Contains("engagement-letter.txt", listing);

                var fetched = await tools.FetchFromM365("item-1", "engagement-letter.txt");
                Assert.Contains("File id:", fetched);
                var fileId = Guid.Parse(fetched.Split("File id:")[1].Split('.')[0].Trim());
                var stored = await scope.ServiceProvider.GetRequiredService<IFileStore>().FindAsync(fileId);
                Assert.Equal("connector:msgraph", stored!.Source);
            }

            // A DIFFERENT user has no session — delegated auth is strictly per user.
            using (var scope = await UserScopeAsync("it-colleague"))
            {
                var tools = scope.ServiceProvider.GetRequiredService<MsGraphTools>();
                Assert.Contains("/api/connectors/msgraph/oauth/start", await tools.ListM365Files());
            }

            // Disable REVOKES: the login rows are gone; re-enabling forces re-auth.
            var disabled = await admin.PostAsync("/api/admin/connectors/msgraph/disable", null);
            disabled.EnsureSuccessStatusCode();
            Assert.Contains("\"revokedLogins\":1", await disabled.Content.ReadAsStringAsync());

            (await admin.PostAsync("/api/admin/connectors/msgraph/enable", null)).EnsureSuccessStatusCode();
            using (var scope = await UserScopeAsync("it-user"))
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                Assert.False(await db.UserConnectorLogins.AnyAsync(l => l.ConnectorId == "msgraph"));

                var tools = scope.ServiceProvider.GetRequiredService<MsGraphTools>();
                Assert.Contains("/api/connectors/msgraph/oauth/start", await tools.ListM365Files());
            }
        }
        finally
        {
            await admin.PostAsync("/api/admin/connectors/msgraph/disable", null);
        }
    }

    /// <summary>A scope acting as a JIT-provisioned dev-tenant user with wildcard permissions.</summary>
    private async Task<IServiceScope> UserScopeAsync(string subject)
    {
        using (var warmup = fixture.Factory.CreateClient())
        {
            warmup.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
            warmup.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
            warmup.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
            (await warmup.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();
        }

        var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();

        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == subject);
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
        return scope;
    }
}

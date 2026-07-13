using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The end-user "connected accounts" surface: GET /api/connectors lists only tenant-enabled
/// DELEGATED connectors with the caller's own connection state, and DELETE …/login unlinks only
/// the caller. Service-mode connectors (local-folder, azure-blob, plenipo-peer) never appear —
/// there is nothing for an individual user to connect.
/// </summary>
[Collection("api")]
public sealed class ConnectedAccountsTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task List_shows_delegated_connectors_per_user_and_disconnect_unlinks_only_me()
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
            using var user = fixture.ClientFor("user");

            // Enabled + delegated → listed, not yet connected. Service-mode connectors are absent.
            var list = await user.GetFromJsonAsync<JsonElement>("/api/connectors");
            var entries = list.EnumerateArray().ToArray();
            var mine = entries.Single(e => e.GetProperty("id").GetString() == "msgraph");
            Assert.False(mine.GetProperty("connected").GetBoolean());
            Assert.DoesNotContain(entries, e => e.GetProperty("id").GetString() == "local-folder");

            // Nothing to unlink yet.
            var early = await user.DeleteAsync("/api/connectors/msgraph/login");
            Assert.Equal(HttpStatusCode.NotFound, early.StatusCode);

            // Link through the real start→callback flow (fake IdP), then the list flips.
            var start = await user.GetFromJsonAsync<JsonElement>("/api/connectors/msgraph/oauth/start");
            var authorizeUrl = start.GetProperty("authorizeUrl").GetString()!;
            var state = HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query)["state"]!;

            // OAuth state is bound to the initiating Plenipo user. A colleague cannot be tricked
            // into consuming it and linking the initiator's provider account to their session.
            using var stateThief = fixture.Factory.CreateClient();
            stateThief.DefaultRequestHeaders.Add("X-Dev-Subject", "it-oauth-state-thief");
            stateThief.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
            stateThief.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
            var stolen = await stateThief.GetAsync(
                $"/api/connectors/msgraph/oauth/callback?code=stolen&state={Uri.EscapeDataString(state)}");
            Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);

            (await user.GetAsync(
                $"/api/connectors/msgraph/oauth/callback?code=abc123&state={Uri.EscapeDataString(state)}"))
                .EnsureSuccessStatusCode();

            list = await user.GetFromJsonAsync<JsonElement>("/api/connectors");
            Assert.True(list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "msgraph")
                .GetProperty("connected").GetBoolean());

            // Delegated auth is strictly per user: a colleague still shows unconnected.
            using var colleague = fixture.Factory.CreateClient();
            colleague.DefaultRequestHeaders.Add("X-Dev-Subject", "it-colleague-accounts");
            colleague.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
            colleague.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
            var theirs = await colleague.GetFromJsonAsync<JsonElement>("/api/connectors");
            Assert.False(theirs.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "msgraph")
                .GetProperty("connected").GetBoolean());

            // Disconnect unlinks ONLY the caller, and is idempotent about being gone.
            var disconnect = await user.DeleteAsync("/api/connectors/msgraph/login");
            Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);
            list = await user.GetFromJsonAsync<JsonElement>("/api/connectors");
            Assert.False(list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "msgraph")
                .GetProperty("connected").GetBoolean());
        }
        finally
        {
            await admin.PostAsync("/api/admin/connectors/msgraph/disable", null);
        }

        // Disabled again → the connector drops off the user's list entirely.
        using var after = fixture.ClientFor("user");
        var final = await after.GetFromJsonAsync<JsonElement>("/api/connectors");
        Assert.DoesNotContain(final.EnumerateArray(), e => e.GetProperty("id").GetString() == "msgraph");
    }
}

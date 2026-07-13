using System.Net.Http.Json;
using System.Text.Json;
using System.Web;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The Google Drive connector — the first delegated connector with a NON-Entra OAuth shape:
/// fixed authorize/token URLs from the manifest templates (no Authority setting at all), fixed
/// query params preserved (access_type=offline, prompt=consent), and the same per-user
/// token-riding tools as Microsoft 365. Keyless via the shared fake IdP + a fake Drive.
/// </summary>
[Collection("api")]
public sealed class GoogleDriveConnectorTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task ConnectFlow_UsesGoogleUrlShape_AndToolsRideTheUserToken()
    {
        using var admin = fixture.ClientFor("system_admin");

        // Stage 1: no Authority setting — Google's URLs are fixed in the manifest.
        (await admin.PutAsJsonAsync("/api/admin/connectors/google-drive/settings", new
        {
            values = new Dictionary<string, string?>
            {
                ["ClientId"] = "google-client-123",
                ["Scopes"] = "https://www.googleapis.com/auth/drive.readonly",
            },
        })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/google-drive/enable", null)).EnsureSuccessStatusCode();

        try
        {
            using var user = fixture.ClientFor("user");

            // The start endpoint builds GOOGLE's authorize URL: fixed host, the manifest's fixed
            // params preserved, standard PKCE params appended with '&' (the template has a '?').
            var start = await user.GetFromJsonAsync<JsonElement>("/api/connectors/google-drive/oauth/start");
            var authorizeUrl = start.GetProperty("authorizeUrl").GetString()!;
            Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?access_type=offline&prompt=consent&", authorizeUrl);
            Assert.Contains("client_id=google-client-123", authorizeUrl);
            Assert.Contains("code_challenge=", authorizeUrl);
            Assert.Contains("scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fdrive.readonly", authorizeUrl);

            // Callback (fake IdP exchanges the code) → tools ride the user's token against fake Drive.
            var state = HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query)["state"]!;
            (await user.GetAsync($"/api/connectors/google-drive/oauth/callback?code=g123&state={Uri.EscapeDataString(state)}"))
                .EnsureSuccessStatusCode();

            // The connected-accounts list shows Drive alongside Microsoft 365, connected.
            var list = await user.GetFromJsonAsync<JsonElement>("/api/connectors");
            var drive = list.EnumerateArray().Single(e => e.GetProperty("id").GetString() == "google-drive");
            Assert.True(drive.GetProperty("connected").GetBoolean());
        }
        finally
        {
            await admin.PostAsync("/api/admin/connectors/google-drive/disable", null);
        }
    }

    [Fact]
    public async Task MissingClientId_Returns409_NotAnEntraShapedAuthorityDemand()
    {
        using var admin = fixture.ClientFor("system_admin");
        // Explicitly clear any ClientId a sibling test configured — this test needs the
        // unconfigured state regardless of execution order.
        (await admin.PutAsJsonAsync("/api/admin/connectors/google-drive/settings", new
        {
            values = new Dictionary<string, string?> { ["ClientId"] = "" },
        })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/google-drive/enable", null)).EnsureSuccessStatusCode();

        try
        {
            // Enabled but unconfigured: the start endpoint refuses with guidance — and must NOT
            // demand an Authority (Google has none).
            using var user = fixture.ClientFor("user");
            using var response = await user.GetAsync("/api/connectors/google-drive/oauth/start");
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            await admin.PostAsync("/api/admin/connectors/google-drive/disable", null);
        }
    }
}

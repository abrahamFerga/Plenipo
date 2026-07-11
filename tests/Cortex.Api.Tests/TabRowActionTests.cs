using System.Text.Json;
using Xunit;

namespace Cortex.Api.Tests;

/// <summary>
/// Per-row tab actions on the wire: a tab's row actions ship with their {field} endpoint template
/// intact (the shell resolves it per row), and — like editors and tab-level actions — an action the
/// caller lacks permission for is omitted from the payload entirely, never merely disabled.
/// </summary>
public sealed class TabRowActionTests : IClassFixture<CortexApiFactory>
{
    private readonly CortexApiFactory _factory;

    public TabRowActionTests(CortexApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    private async Task<JsonElement> ItemsTabFor(HttpClient client)
    {
        var response = await client.GetAsync("/api/platform/modules");
        response.EnsureSuccessStatusCode();
        var modules = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var test = modules.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "test");
        return test.GetProperty("tabs").EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == "items");
    }

    [Fact]
    public async Task Row_actions_ship_with_their_endpoint_template_intact()
    {
        // system_admin holds "*", so both row actions are visible.
        using var client = ClientAs("system_admin", "rowact-admin");
        var tab = await ItemsTabFor(client);

        var actions = tab.GetProperty("rowActions").EnumerateArray().ToArray();
        Assert.Equal(2, actions.Length);

        var approve = actions.Single(a => a.GetProperty("id").GetString() == "approve");
        // The template is NOT resolved server-side — the shell substitutes {id} per row.
        Assert.Equal("/api/test/items/{id}/approve", approve.GetProperty("endpointTemplate").GetString());
        Assert.Equal("Approve this item?", approve.GetProperty("confirm").GetString());
    }

    [Fact]
    public async Task A_row_action_the_caller_lacks_permission_for_is_omitted_not_disabled()
    {
        // A plain user sees the (ungated) tab, and the ungated approve action — but the
        // permission-gated retire action is absent from the payload entirely.
        using var client = ClientAs("user", "rowact-plain-user");
        var tab = await ItemsTabFor(client);

        var action = Assert.Single(tab.GetProperty("rowActions").EnumerateArray());
        Assert.Equal("approve", action.GetProperty("id").GetString());
    }
}

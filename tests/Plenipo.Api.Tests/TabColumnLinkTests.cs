using System.Text.Json;
using Plenipo.Modules.Sdk;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// Link-capable columns on the wire: a column may make its value navigable, and — like a row
/// action's endpoint — the <c>{field}</c> template ships UNRESOLVED for the shell to substitute per
/// row. A column that declares no link stays exactly what it always was, which is the whole point:
/// this is additive, and every existing manifest keeps rendering plain text.
/// </summary>
public sealed class TabColumnLinkTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public TabColumnLinkTests(PlenipoApiFactory factory) => _factory = factory;

    [Fact]
    public void A_column_declares_no_link_by_default()
    {
        var column = new TabColumn("name", "Name");

        Assert.Null(column.LinkTemplate);
        Assert.False(column.Masked);
    }

    [Fact]
    public void The_primary_constructor_still_takes_exactly_three_parameters()
    {
        // LinkTemplate is an init-only property, NOT a fourth positional parameter, so a product
        // already compiled against a published package keeps binding to this constructor and this
        // Deconstruct. Adding a positional parameter here would be binary-breaking, and the failure
        // would surface at runtime in the consumer, not at build time here.
        var column = new TabColumn("number", "Number", true) { LinkTemplate = "/finance/accounts" };
        var (field, header, masked) = column;

        Assert.Equal("number", field);
        Assert.Equal("Number", header);
        Assert.True(masked);
        Assert.Equal("/finance/accounts", column.LinkTemplate);
    }

    [Fact]
    public async Task A_columns_link_template_reaches_the_client_with_its_placeholder_intact()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "collink-admin");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");

        var response = await client.GetAsync("/api/platform/modules");
        response.EnsureSuccessStatusCode();
        var modules = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var tab = modules.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "test")
            .GetProperty("tabs").EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == "items");

        var columns = tab.GetProperty("columns").EnumerateArray().ToArray();

        var owner = columns.Single(c => c.GetProperty("field").GetString() == "owner");
        Assert.Equal("/test/owners?focus={ownerId}", owner.GetProperty("linkTemplate").GetString());

        // The unlinked column carries a null link rather than an absent property, so a client can
        // read the field uniformly.
        var name = columns.Single(c => c.GetProperty("field").GetString() == "name");
        Assert.Equal(JsonValueKind.Null, name.GetProperty("linkTemplate").ValueKind);
    }
}

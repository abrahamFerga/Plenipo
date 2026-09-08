using System.Text.Json;
using Plenipo.Testing.Conformance;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// #208: what the tenancy pack compares. "A second tenant sees none of the first tenant's rows" is
/// decided by ids on a seeded surface and by row count everywhere else, so the row reading and the
/// id comparison are pinned here, without a host.
/// </summary>
public sealed class TenantRowsTests
{
    [Fact]
    public void Rows_ReadsAnArrayOrTheFirstArrayValuedListProperty()
    {
        Assert.Equal(2, TenantRows.Rows(Parse("[{\"id\":1},{\"id\":2}]")).Count);
        Assert.Single(TenantRows.Rows(Parse("{\"total\":9,\"items\":[{\"id\":\"a\"}]}")));
        Assert.Empty(TenantRows.Rows(Parse("{\"count\":3}")));
        Assert.Empty(TenantRows.Rows(Parse("\"text\"")));
    }

    [Fact]
    public void SharedIds_NamesTheRowsBothTenantsWereShown()
    {
        var first = Parse("[{\"id\":\"a\"},{\"id\":\"b\"},{\"id\":3}]");

        // A fresh tenant's own seeded rows: different ids, nothing shared.
        Assert.Empty(TenantRows.SharedIds(first, Parse("[{\"id\":\"c\"},{\"id\":4}]")));

        // A leak: the second tenant was shown two of the first tenant's rows.
        Assert.Equal(new[] { "b", "3" }, TenantRows.SharedIds(first, Parse("{\"items\":[{\"id\":\"b\"},{\"id\":3},{\"id\":\"z\"}]}")));

        // A row without an id cannot be attributed; the pack refuses those on a seeded surface.
        Assert.Null(TenantRows.IdOf(Parse("{\"name\":\"no id\"}")));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

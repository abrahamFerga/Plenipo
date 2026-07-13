using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>The one-call ops snapshot: shape, tenant scoping via RBAC, and gating.</summary>
[Collection("api")]
public sealed class OpsEndpointTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Ops_ReturnsTheFullHealthSnapshot()
    {
        using var response = await fixture.ClientFor("tenant_admin").GetAsync("/api/admin/ops");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.True(root.GetProperty("jobs").GetProperty("queued").GetInt32() >= 0);
        Assert.True(root.GetProperty("rag").GetProperty("collections").GetInt32() >= 0);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("connectors").ValueKind);
        Assert.Equal(JsonValueKind.False, root.GetProperty("notifications").GetProperty("webhookConfigured").ValueKind is JsonValueKind.True
            ? JsonValueKind.False // configured by another test — either boolean is a valid shape
            : root.GetProperty("notifications").GetProperty("webhookConfigured").ValueKind);
        Assert.Equal("Mock", root.GetProperty("ai").GetProperty("provider").GetString());
        Assert.True(root.GetProperty("ai").GetProperty("monthTokens").GetInt64() >= 0);
    }

    [Fact]
    public async Task Ops_IsGatedByViewAuditLog()
    {
        using var response = await fixture.ClientFor("user").GetAsync("/api/admin/ops");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

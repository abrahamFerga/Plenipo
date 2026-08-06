using System.Net;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// End-to-end proof, through the real host and a real database, that the hub transport authenticates as
/// the caller rather than as the dev-auth defaults.
/// <para>
/// A browser's WebSocket handshake cannot set request headers, so SignalR can only carry the dev identity
/// in the query string. Before this was read, every <c>/hubs/agent</c> turn in Development resolved to
/// subject <c>dev-user</c>, tenant <c>dev</c>, roles <c>system_admin</c> ⇒ <c>["*"]</c> — so RBAC-shaped
/// behaviour verified over the hub proved nothing about RBAC.
/// </para>
/// <para>
/// The observable is JIT provisioning: <c>RequestEnricher</c> provisions a user from the authenticated
/// principal on the hub path and the REST path alike, so "which subject exists afterwards" reports exactly
/// which principal the pipeline resolved — no SignalR client or WebSocket upgrade needed to see it.
/// </para>
/// </summary>
[Collection("api")]
public sealed class AgentHubQueryIdentityTests(IntegrationFixture fixture)
{
    /// <summary>Subjects provisioned so far, read past the tenant filter (this asks a global question).</summary>
    private async Task<string[]> ProvisionedSubjectsAsync()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await platform.Users.IgnoreQueryFilters().Select(u => u.Subject).ToArrayAsync();
    }

    [Fact]
    public async Task HubPath_AuthenticatesAsTheQueryStringIdentity()
    {
        // A bare client: no X-Dev-* headers at all, exactly like a browser WebSocket handshake.
        using var client = fixture.Factory.CreateClient();

        var response = await client.PostAsync(
            "/hubs/agent/negotiate?negotiateVersion=1"
                + "&X-Dev-Subject=hub-analyst&X-Dev-Tenant=dev&X-Dev-Roles=user",
            content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hub-analyst", await ProvisionedSubjectsAsync());
    }

    /// <summary>
    /// The hub-path restriction is the safety half, mirroring the JwtBearer <c>access_token</c> rule in
    /// <c>AuthSetup</c>: a query string reaches browser history, proxy logs and error reports, and the REST
    /// surface can carry headers perfectly well, so it must keep ignoring identity in the URL.
    /// </summary>
    [Fact]
    public async Task RestPath_IgnoresQueryStringIdentity()
    {
        using var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync(
            "/api/platform/modules?X-Dev-Subject=rest-escalated&X-Dev-Tenant=dev&X-Dev-Roles=system_admin");

        // The request still succeeds — it authenticated, just as the DEFAULT dev principal, which is the
        // point: the URL parameters changed nothing.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("rest-escalated", await ProvisionedSubjectsAsync());
    }
}

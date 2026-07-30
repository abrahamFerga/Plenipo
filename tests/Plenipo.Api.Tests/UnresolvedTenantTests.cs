using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The second half of issue #70. Permissions are resolved only AFTER a tenant resolves, so an
/// authenticated principal whose tenant does not exist carries an empty permission set: it got a cheerful
/// 200 from <c>/api/platform/me</c> and a bare 403 from everything else. A client shell therefore rendered
/// normally and then failed every call, with nothing anywhere naming the cause — and on a fresh deployment
/// that is the entire symptom of having no tenant at all.
///
/// <para>The fix is strictly diagnostic: no status code and no authorization decision changes.</para>
/// </summary>
public sealed class UnresolvedTenantTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public UnresolvedTenantTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientOn(string tenant, string subject, string roles = "system_admin")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", roles);
        return client;
    }

    [Fact]
    public async Task Me_reports_that_the_tenant_did_not_resolve()
    {
        using var client = ClientOn("no-such-tenant", "stranded-user");

        var response = await client.GetAsync("/api/platform/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // still 200 — the shell needs to render

        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(me.GetProperty("tenantResolved").GetBoolean());
        Assert.Contains("no-such-tenant", me.GetProperty("tenantProblem").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Me_reports_a_resolved_tenant_as_resolved()
    {
        using var client = ClientOn("dev", "settled-user");

        var me = await client.GetFromJsonAsync<JsonElement>("/api/platform/me");

        Assert.True(me.GetProperty("tenantResolved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("tenantProblem").ValueKind);
    }

    [Fact]
    public async Task Me_reports_the_signed_in_subject()
    {
        using var client = ClientOn("dev", "subject-under-test");

        var me = await client.GetFromJsonAsync<JsonElement>("/api/platform/me");

        Assert.Equal("subject-under-test", me.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task A_forbidden_response_names_the_unresolved_tenant()
    {
        using var client = ClientOn("no-such-tenant", "stranded-admin");

        var response = await client.GetAsync("/api/admin/tenants");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // the DECISION is unchanged
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("no-such-tenant", problem.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Tenant not resolved", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_forbidden_body_never_reveals_how_many_tenants_exist()
    {
        // It echoes only the slug the caller themselves supplied. A count would leak deployment shape to
        // an authenticated principal who has been granted nothing.
        using var client = ClientOn("no-such-tenant", "nosy-user");

        var body = await (await client.GetAsync("/api/admin/tenants")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("tenants exist", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exactly one", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_ordinary_permission_denial_is_unchanged()
    {
        // The regression guard for the new result handler: a resolved tenant with insufficient permission
        // must still produce a plain 403 with no invented body.
        using var client = ClientOn("dev", "ordinary-user", roles: "user");

        var response = await client.GetAsync("/api/admin/tenants");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Tenant not resolved", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_still_answers_without_a_resolved_tenant()
    {
        // Deliberate coupling guard: /api/platform/info needs authentication but no permission, and is
        // documented as answering from the deployment defaults when no tenant resolves. The new handler
        // must not have turned that into a 403.
        using var client = ClientOn("no-such-tenant", "info-reader");

        var response = await client.GetAsync("/api/platform/info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

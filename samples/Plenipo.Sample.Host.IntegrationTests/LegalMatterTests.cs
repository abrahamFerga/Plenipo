using System.Net;
using System.Net.Http.Json;
using Plenipo.Application.Agents;
using Plenipo.Application.Files;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// End-to-end coverage for the Legal module's matter workspace (v1 items 1–2 of the legal vertical
/// plan): matters as tenant-scoped engagement workspaces, documents attached via the platform file
/// store, and the live Matters tab endpoints. All keyless — real Postgres via Testcontainers, real
/// permission-filtered tool surface, no AI provider needed.
/// </summary>
[Collection("api")]
public sealed class LegalMatterTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Matter_lifecycle_create_attach_list_through_the_tool_surface()
    {
        using var scope = await DevUserScopeAsync();
        var tools = scope.ServiceProvider.GetRequiredService<MatterTools>();
        var files = scope.ServiceProvider.GetRequiredService<IFileStore>();

        // The agent's tool surface for the legal module includes the matter tools.
        var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
        var toolNames = registry.GetModuleTools("legal", scope.ServiceProvider).Select(t => t.Name).ToList();
        Assert.Contains("create_matter", toolNames);
        Assert.Contains("attach_document_to_matter", toolNames);

        // Create the matter, store a "brief", attach it — the documented lawyer flow.
        var created = await tools.CreateMatter("Julia Assange defense", "J. Assange");
        Assert.Contains("Created matter 'Julia Assange defense'", created);

        using var content = new MemoryStream("Exhibit A: the extradition brief."u8.ToArray());
        var stored = await files.SaveAsync("extradition-brief.txt", "text/plain", content, source: "upload");

        // Matter resolution is case-insensitive — the model rarely echoes exact casing.
        var attached = await tools.AttachDocumentToMatter(stored.Id.ToString(), "JULIA assange DEFENSE");
        Assert.Contains($"file id: {stored.Id}", attached);

        // Attaching twice is caught, not duplicated.
        var again = await tools.AttachDocumentToMatter(stored.Id.ToString(), "Julia Assange defense");
        Assert.Contains("already attached", again);

        // The listing tools hand the agent everything read_document needs.
        Assert.Contains("Julia Assange defense", await tools.ListMatters());
        var documents = await tools.ListMatterDocuments("julia assange defense");
        Assert.Contains("extradition-brief.txt", documents);
        Assert.Contains(stored.Id.ToString(), documents);
    }

    [Fact]
    public async Task Matters_tab_endpoints_serve_the_matter_and_its_documents()
    {
        using (var scope = await DevUserScopeAsync())
        {
            var tools = scope.ServiceProvider.GetRequiredService<MatterTools>();
            var files = scope.ServiceProvider.GetRequiredService<IFileStore>();
            await tools.CreateMatter("Acme / Initech NDA", "Acme Corp");
            using var content = new MemoryStream("the NDA draft"u8.ToArray());
            var stored = await files.SaveAsync("nda-draft.txt", "text/plain", content, source: "upload");
            await tools.AttachDocumentToMatter(stored.Id.ToString(), "Acme / Initech NDA");
        }

        using var client = fixture.ClientFor("system_admin");

        var matters = await client.GetFromJsonAsync<List<MatterRow>>("/api/legal/matters");
        var matter = Assert.Single(matters!, m => m.Name == "Acme / Initech NDA");
        Assert.Equal("Acme Corp", matter.ClientName);
        Assert.Equal(1, matter.DocumentCount);

        var docs = await client.GetFromJsonAsync<List<DocRow>>($"/api/legal/matters/{matter.Id}/documents");
        Assert.Equal("nda-draft.txt", Assert.Single(docs!).FileName);
    }

    [Fact]
    public async Task Matters_are_tenant_isolated_and_permission_gated()
    {
        await fixture.EnsureTenantAsync("firm-b");

        using (var scope = await DevUserScopeAsync())
        {
            var tools = scope.ServiceProvider.GetRequiredService<MatterTools>();
            await tools.CreateMatter("Dev-tenant-only matter");
        }

        // Another tenant's admin sees an empty list — the query filter, not the endpoint, isolates.
        using var otherTenant = fixture.ClientForTenant("system_admin", "firm-b");
        var foreign = await otherTenant.GetFromJsonAsync<List<MatterRow>>("/api/legal/matters");
        Assert.DoesNotContain(foreign!, m => m.Name == "Dev-tenant-only matter");

        // A plain user without legal.matters.view is refused outright.
        using var user = fixture.ClientFor("user");
        var response = await user.GetAsync("/api/legal/matters");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Attaching_to_a_missing_matter_or_file_guides_the_agent()
    {
        using var scope = await DevUserScopeAsync();
        var tools = scope.ServiceProvider.GetRequiredService<MatterTools>();

        Assert.Contains("not a valid file id", await tools.AttachDocumentToMatter("not-a-guid", "whatever"));
        Assert.Contains("No stored file", await tools.AttachDocumentToMatter(Guid.NewGuid().ToString(), "whatever"));

        using var content = new MemoryStream("x"u8.ToArray());
        var files = scope.ServiceProvider.GetRequiredService<IFileStore>();
        var stored = await files.SaveAsync("orphan.txt", "text/plain", content, source: "upload");
        Assert.Contains("No matter named", await tools.AttachDocumentToMatter(stored.Id.ToString(), "ghost matter"));
    }

    /// <summary>A scope acting as the JIT-provisioned dev user (tenant + user + wildcard permissions).</summary>
    private async Task<IServiceScope> DevUserScopeAsync()
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
        var user = await db.Users.FirstAsync(u => u.Subject == "it-user");
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
        return scope;
    }

    private sealed record MatterRow(Guid Id, string Name, string? ClientName, string Status, int DocumentCount);

    private sealed record DocRow(Guid FileId, string FileName, string? Note);
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Files;
using Plenipo.Infrastructure.Documents;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// End-to-end coverage for the tenant clause library + firm playbook (legal v1 item 4) and the
/// drafting-to-work-product chain (item 5): draft_clause → generate_pdf → attach_document_to_matter,
/// executed exactly as the model chains them — all keyless.
/// </summary>
[Collection("api")]
public sealed class LegalLibraryTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Library_and_playbook_are_seeded_and_served()
    {
        using var admin = fixture.ClientFor("system_admin");

        // Seeding populated the dev tenant's library from the catalog defaults…
        var clauses = await admin.GetFromJsonAsync<JsonElement>("/api/legal/clauses");
        Assert.True(clauses.GetArrayLength() >= 8);

        // …and the default playbook, ordered most-severe first for the tab.
        var playbook = await admin.GetFromJsonAsync<JsonElement>("/api/legal/playbook");
        Assert.True(playbook.GetArrayLength() >= 5);
        Assert.Equal("Critical", playbook[0].GetProperty("severity").GetString());
    }

    [Fact]
    public async Task Firm_curation_flows_into_search_and_drafting()
    {
        using var admin = fixture.ClientFor("system_admin");

        // The firm adds its own precedent clause…
        var upsert = await admin.PostAsJsonAsync("/api/legal/clauses", new LegalModule.UpsertClauseRequest(
            "escrow", "Source Code Escrow", "Protection",
            "Escrow of source code with release on insolvency.",
            "{PartyA} shall deposit the source code with an escrow agent for the benefit of {PartyB}, released upon {PartyA}'s insolvency."));
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);

        // …and the DB-backed tools pick it up immediately.
        using var scope = await DevUserScopeAsync();
        var tools = scope.ServiceProvider.GetRequiredService<LegalTools>();

        Assert.Contains("Source Code Escrow", await tools.SearchClauses("escrow"));

        var draft = await tools.DraftClause("escrow", "Initech", "Acme Corp");
        Assert.Contains("Initech", draft);
        Assert.Contains("not legal advice", draft, StringComparison.OrdinalIgnoreCase);

        // Unknown clause types guide the agent to search first.
        Assert.Contains("search_clauses", await tools.DraftClause("teleportation rights", "A", "B"));

        // The playbook tool narrates every rule with severity for the reviewing agent.
        var playbook = await tools.GetPlaybook();
        Assert.Contains("[Critical]", playbook);
        Assert.Contains("Uncapped liability", playbook);
    }

    [Fact]
    public async Task Library_management_requires_the_manage_permission()
    {
        using var user = fixture.ClientFor("user");

        var response = await user.PostAsJsonAsync("/api/legal/clauses", new LegalModule.UpsertClauseRequest(
            "rogue", "Rogue", "X", "Should not be writable.", "{PartyA} {PartyB}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Drafting_chains_into_stored_pdf_work_product_on_the_matter()
    {
        using var scope = await DevUserScopeAsync();
        var legal = scope.ServiceProvider.GetRequiredService<LegalTools>();
        var matters = scope.ServiceProvider.GetRequiredService<MatterTools>();
        var documents = scope.ServiceProvider.GetRequiredService<DocumentTools>();

        // The exact chain the agent instructions prescribe: draft → generate_pdf → attach.
        await matters.CreateMatter("Initech engagement", "Initech");
        var draft = await legal.DraftClause("confidentiality", "Initech", "Acme Corp");

        var generated = await documents.GeneratePdf("Confidentiality clause — Initech", draft, "initech-confidentiality.pdf");
        var fileId = generated.Split("File id:")[1].Split('.')[0].Trim();

        var attached = await matters.AttachDocumentToMatter(fileId, "initech engagement", "AI draft for attorney review");
        Assert.Contains("Attached 'initech-confidentiality.pdf'", attached);

        // The work product is real: read it back through the same tool surface and find the draft text.
        var text = await documents.ReadDocument(fileId);
        Assert.Contains("Initech", text);
        Assert.Contains("Confidentiality", text);

        // Provenance survives: the matter lists it, and the stored file records its tool origin.
        Assert.Contains("initech-confidentiality.pdf", await matters.ListMatterDocuments("Initech engagement"));
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var stored = await db.StoredFiles.IgnoreQueryFilters().FirstAsync(f => f.Id == Guid.Parse(fileId));
        Assert.Equal("generate_pdf", stored.Source);
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
}

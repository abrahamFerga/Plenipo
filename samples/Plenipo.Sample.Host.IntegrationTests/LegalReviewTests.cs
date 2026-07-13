using Plenipo.Application.Files;
using Plenipo.Infrastructure.Documents;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// End-to-end coverage for playbook contract review (legal v1 item 6): the exact tool chain the
/// agent instructions prescribe — read the contract, get the playbook, write the red-flag memo as a
/// PDF, attach it to the matter — executed deterministically against real storage. Keyless as always.
/// </summary>
[Collection("api")]
public sealed class LegalReviewTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Playbook_review_chain_produces_a_redflag_memo_on_the_matter()
    {
        using var scope = await DevUserScopeAsync();
        var files = scope.ServiceProvider.GetRequiredService<IFileStore>();
        var documents = scope.ServiceProvider.GetRequiredService<DocumentTools>();
        var legal = scope.ServiceProvider.GetRequiredService<LegalTools>();
        var matters = scope.ServiceProvider.GetRequiredService<MatterTools>();

        // A client contract arrives (via chat or WhatsApp — same file store either way). Its terms
        // deliberately trip two default playbook rules: unilateral termination, no liability cap.
        const string contract = """
            SERVICES AGREEMENT between Initech (Provider) and Acme Corp (Client).
            1. Provider may terminate this Agreement at any time without notice.
            2. Client shall pay all invoices within 10 days.
            3. This Agreement contains no limitation of liability.
            """;
        using var upload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(contract));
        var stored = await files.SaveAsync("services-agreement.txt", "text/plain", upload, source: "upload");

        await matters.CreateMatter("Acme services deal", "Acme Corp");
        await matters.AttachDocumentToMatter(stored.Id.ToString(), "Acme services deal", "client draft");

        // --- The review chain, exactly as the agent drives it ---------------------------------
        var contractText = await documents.ReadDocument(stored.Id.ToString());
        Assert.Contains("terminate this Agreement at any time", contractText);

        var playbook = await legal.GetPlaybook();
        Assert.Contains("Unilateral termination", playbook);

        // The memo a model would compose from those two inputs (deterministic stand-in here),
        // with the citation discipline the instructions demand.
        var memo = $"""
            Red-flag review of services-agreement.txt (file id: {stored.Id})

            [Critical] Unilateral termination — clause 1 lets the Provider terminate at any time
            without notice (source: services-agreement.txt, file id: {stored.Id}).

            [Critical] Uncapped liability — the agreement expressly contains no limitation of
            liability (clause 3; source: services-agreement.txt, file id: {stored.Id}).

            Prepared by the Plenipo legal assistant. Not legal advice — attorney review required.
            """;
        var generated = await documents.GeneratePdf("Red-flag memo — Acme services deal", memo, "acme-redflag-memo.pdf");
        var memoFileId = generated.Split("File id:")[1].Split('.')[0].Trim();

        var attached = await matters.AttachDocumentToMatter(memoFileId, "Acme services deal", "playbook review memo");
        Assert.Contains("acme-redflag-memo.pdf", attached);

        // --- The work product is real, cited, and on the matter --------------------------------
        var memoText = await documents.ReadDocument(memoFileId);
        Assert.Contains("Unilateral termination", memoText);
        Assert.Contains(stored.Id.ToString(), memoText); // the citation survives PDF round-trip

        var matterDocs = await matters.ListMatterDocuments("Acme services deal");
        Assert.Contains("services-agreement.txt", matterDocs);
        Assert.Contains("acme-redflag-memo.pdf", matterDocs);
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

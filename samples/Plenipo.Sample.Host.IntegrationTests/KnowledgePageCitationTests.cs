using System.Text;
using Plenipo.Application.Files;
using Plenipo.Application.Rag;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Documents;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Infrastructure.Rag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Page provenance over a REAL multi-page PDF, built with the platform's own renderer and read back
/// through the platform's own extractor — so the test exercises the actual PdfPig round trip rather
/// than a hand-made page map. A legal answer that says "the cap is in clause 8" is only checkable if
/// it can also say which page clause 8 is on.
/// </summary>
[Collection("api")]
public sealed class KnowledgePageCitationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task A_passage_from_a_multi_page_pdf_cites_its_page()
    {
        using var scope = await UserScopeAsync("page-citer");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var collectionId = await rag.GetOrCreateCollectionAsync(
            "knowledge", null, null, "pages: agreement", language: "english");

        // Three pages, each with a distinctive phrase, padded so the renderer really paginates.
        var pdf = DocumentTools.BuildPdf("Master Services Agreement", string.Join(
            "\n\n",
            Page("The parties record the commencement date and the initial term of this agreement.", "AAA"),
            Page("Termination for convenience requires ninety days of prior written notice.", "BBB"),
            Page("Liability under this agreement is capped at the fees paid in the trailing year.", "CCC")));

        var fileId = await StoreAsync(scope, "msa.pdf", "application/pdf", pdf);

        // The extractor must see three pages before anything downstream can cite one.
        var extracted = await scope.ServiceProvider
            .GetRequiredService<Plenipo.Application.Documents.IDocumentReader>()
            .ExtractAsync(fileId);
        Assert.NotNull(extracted);
        Assert.True(extracted.Pages.Count >= 3, $"expected a paginated PDF, got {extracted.Pages.Count} page(s)");

        Assert.True(await rag.IngestFileAsync(collectionId, fileId) > 0);

        // Every stored chunk carries a page, and the pages are within the document.
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var stored = await db.RagChunks.Where(c => c.FileId == fileId).OrderBy(c => c.Ordinal).ToListAsync();
        Assert.All(stored, c => Assert.NotNull(c.PageFrom));
        Assert.All(stored, c => Assert.InRange(c.PageFrom!.Value, 1, extracted.Pages.Count));
        Assert.All(stored, c => Assert.True(c.PageTo >= c.PageFrom, "a page range must not run backwards"));

        // Pages advance with the document: the last passage is not on an earlier page than the first.
        Assert.True(stored[^1].PageFrom >= stored[0].PageFrom);
        // ...and the document genuinely spans pages, or the rest proves nothing.
        Assert.True(stored[^1].PageFrom > stored[0].PageFrom, "the fixture PDF did not paginate");

        // The page a retrieved passage claims must be the page the extractor puts that text on.
        // Derived rather than hard-coded: pinning "page 2" would test PdfPig's line breaking, and
        // what is under test here is the offset-to-page mapping.
        var clause = extracted.Text.IndexOf("ninety days", StringComparison.OrdinalIgnoreCase);
        Assert.True(clause >= 0, "the fixture text did not survive extraction");
        var (expectedPage, _) = extracted.PagesFor(clause, clause + "ninety days".Length);

        var hits = await rag.SearchAsync("termination for convenience notice period");
        var hit = Assert.Single(hits, h => h.Text.Contains("ninety days", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expectedPage, hit.PageFrom);
        Assert.NotNull(hit.PageCitation);

        // And the agent sees it in the citation line, which is the whole point.
        var answer = await scope.ServiceProvider.GetRequiredService<RagTools>()
            .SearchKnowledge("termination for convenience notice period");
        Assert.Contains("msa.pdf", answer);
        Assert.Contains(hit.PageCitation!, answer);
    }

    [Fact]
    public async Task A_plain_text_document_cites_the_file_without_inventing_a_page()
    {
        using var scope = await UserScopeAsync("page-citer");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var collectionId = await rag.GetOrCreateCollectionAsync(
            "knowledge", null, null, "pages: plain notes", language: "english");

        var fileId = await StoreAsync(scope, "notes.txt", "text/plain",
            Encoding.UTF8.GetBytes("The escalation contact for priority incidents is the duty engineer on call."));
        Assert.True(await rag.IngestFileAsync(collectionId, fileId) > 0);

        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var stored = await db.RagChunks.Where(c => c.FileId == fileId).ToListAsync();
        Assert.All(stored, c => Assert.Null(c.PageFrom));

        var hits = await rag.SearchAsync("escalation contact for priority incidents");
        var hit = Assert.Single(hits, h => h.FileName == "notes.txt");
        Assert.Null(hit.PageCitation);

        // No stray "p." in the citation line for a document that has no pages.
        var answer = await scope.ServiceProvider.GetRequiredService<RagTools>()
            .SearchKnowledge("escalation contact for priority incidents");
        Assert.Contains("notes.txt (file id:", answer);
    }

    [Fact]
    public async Task Re_indexing_keeps_the_page_a_passage_was_found_on()
    {
        using var scope = await UserScopeAsync("page-citer");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var collectionId = await rag.GetOrCreateCollectionAsync(
            "knowledge", null, null, "pages: stability", language: "english");

        var pdf = DocumentTools.BuildPdf("Handbook", string.Join(
            "\n\n",
            Page("Expense claims must be filed within thirty days of the expenditure.", "DDD"),
            Page("Travel bookings above five thousand require director approval.", "EEE")));
        var fileId = await StoreAsync(scope, "handbook.pdf", "application/pdf", pdf);

        await rag.IngestFileAsync(collectionId, fileId);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var first = await PagesByOrdinalAsync(db, fileId);

        await rag.IngestFileAsync(collectionId, fileId); // idempotent re-ingest
        Assert.Equal(first, await PagesByOrdinalAsync(db, fileId));
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>A page's worth of text: a distinctive sentence plus filler that forces a page break.</summary>
    private static string Page(string sentence, string marker) =>
        sentence + "\n\n" + string.Join("\n\n", Enumerable.Range(0, 22).Select(i => $"{marker} filler line {i} carrying enough words to occupy a printed line of the page."));

    private static async Task<Dictionary<int, (int?, int?)>> PagesByOrdinalAsync(PlatformDbContext db, Guid fileId) =>
        await db.RagChunks
            .Where(c => c.FileId == fileId)
            .OrderBy(c => c.Ordinal)
            .ToDictionaryAsync(c => c.Ordinal, c => (c.PageFrom, c.PageTo));

    private static async Task<Guid> StoreAsync(IServiceScope scope, string name, string contentType, byte[] bytes)
    {
        var files = scope.ServiceProvider.GetRequiredService<IFileStore>();
        using var stream = new MemoryStream(bytes);
        return (await files.SaveAsync(name, contentType, stream, source: "upload")).Id;
    }

    private async Task<IServiceScope> UserScopeAsync(string subject)
    {
        using (var warmup = fixture.Factory.CreateClient())
        {
            warmup.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
            warmup.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
            warmup.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
            (await warmup.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();
        }

        var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();

        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == subject);
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
        return scope;
    }
}

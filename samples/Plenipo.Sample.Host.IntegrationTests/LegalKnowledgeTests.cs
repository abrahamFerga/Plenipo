using Plenipo.Application.Files;
using Plenipo.Application.Jobs;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Infrastructure.Rag;
using Plenipo.Modules.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The RAG core end to end (docs/PLATFORM_CONNECTORS_RAG_PLAN.md, phase 1), keyless on real
/// Postgres + pgvector: index_matter_documents builds a matter-scoped collection through the job
/// runner, search_knowledge answers with cited passages via hybrid (vector + full-text) retrieval,
/// exact identifiers are found lexically, collection scoping holds, re-indexing doesn't duplicate,
/// and the ethical wall (legal item 10) blocks matter tools AND retrieval — fail closed, even for
/// a wildcard-permission user outside the wall.
/// </summary>
[Collection("api")]
public sealed class LegalKnowledgeTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Indexes_a_matter_and_answers_with_cited_scoped_passages()
    {
        using var scope = await UserScopeAsync("it-user");
        var matters = scope.ServiceProvider.GetRequiredService<MatterTools>();

        await matters.CreateMatter("Acme diligence");
        await matters.CreateMatter("Beta cookbook");

        await AttachAsync(scope, matters, "Acme diligence", "acme-msa.txt",
            "Master services agreement with Acme Corp. Either party may terminate this agreement on ninety days " +
            "written notice. Liability is capped at the fees paid in the trailing twelve months. " +
            "The special exhibit is referenced as clause 42-B in the schedule.");
        await AttachAsync(scope, matters, "Beta cookbook", "recipes.txt",
            "The sourdough recipe calls for two cups of flour, a pinch of salt, and patient overnight proofing. " +
            "Serve the bread warm with butter and a drizzle of honey.");

        await IndexAndWaitAsync(matters, "Acme diligence", "it-user");
        await IndexAndWaitAsync(matters, "Beta cookbook", "it-user");

        var knowledge = scope.ServiceProvider.GetRequiredService<RagTools>();

        // Semantic arm: a termination question lands in the MSA, cited, not in the cookbook.
        var answer = await knowledge.SearchKnowledge("What are the termination rights and notice period?");
        Assert.Contains("terminate", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("acme-msa.txt", answer);
        Assert.Contains("file id:", answer);

        // Lexical arm: an exact identifier embeddings would blur is still found (hybrid, RRF).
        var exact = await knowledge.SearchKnowledge("42-B");
        Assert.Contains("clause 42-B", exact);
        Assert.Contains("acme-msa.txt", exact);

        // Collection scoping: inside the cookbook's collection, the MSA does not exist.
        var scoped = await knowledge.SearchKnowledge("termination rights", collection: "matter: Beta cookbook");
        Assert.DoesNotContain("acme-msa.txt", scoped);
    }

    [Fact]
    public async Task Reindexing_a_matter_refreshes_instead_of_duplicating()
    {
        using var scope = await UserScopeAsync("it-user");
        var matters = scope.ServiceProvider.GetRequiredService<MatterTools>();

        await matters.CreateMatter("Reindex target");
        await AttachAsync(scope, matters, "Reindex target", "policy.txt",
            "The retention policy requires archival of all records after seven years of storage.");

        await IndexAndWaitAsync(matters, "Reindex target", "it-user");
        var first = await CountChunksAsync("matter: Reindex target");
        Assert.True(first > 0);

        await IndexAndWaitAsync(matters, "Reindex target", "it-user");
        Assert.Equal(first, await CountChunksAsync("matter: Reindex target"));
    }

    [Fact]
    public async Task Ethical_wall_hides_the_matter_and_its_knowledge_even_from_wildcard_users()
    {
        using var partner = await UserScopeAsync("it-user");
        var partnerMatters = partner.ServiceProvider.GetRequiredService<MatterTools>();

        await partnerMatters.CreateMatter("Confidential merger");
        await AttachAsync(partner, partnerMatters, "Confidential merger", "term-sheet.txt",
            "The confidential merger term sheet fixes the acquisition price at forty million dollars.");
        await IndexAndWaitAsync(partnerMatters, "Confidential merger", "it-user");

        // Before the wall: a colleague (different user, same tenant, wildcard permissions) sees it.
        using (var colleague = await UserScopeAsync("it-colleague"))
        {
            var tools = colleague.ServiceProvider.GetRequiredService<MatterTools>();
            Assert.Contains("Confidential merger", await tools.ListMatters());
        }

        Assert.Contains("ethical wall", await partnerMatters.RestrictMatterAccess("Confidential merger"));

        // After: the matter reads as nonexistent and retrieval fails closed — permissions don't help.
        using (var colleague = await UserScopeAsync("it-colleague"))
        {
            var tools = colleague.ServiceProvider.GetRequiredService<MatterTools>();
            Assert.DoesNotContain("Confidential merger", await tools.ListMatters());
            Assert.Contains("No matter named", await tools.StartBulkReview("Confidential merger", "anything?"));
            Assert.Contains("No matter named", await tools.IndexMatterDocuments("Confidential merger"));

            var knowledge = colleague.ServiceProvider.GetRequiredService<RagTools>();
            var answer = await knowledge.SearchKnowledge("merger acquisition price");
            Assert.DoesNotContain("term-sheet.txt", answer);
            Assert.DoesNotContain("forty million", answer);
        }

        // The partner, inside the wall, still retrieves it.
        var mine = await partner.ServiceProvider.GetRequiredService<RagTools>()
            .SearchKnowledge("merger acquisition price");
        Assert.Contains("term-sheet.txt", mine);

        // Lifting the wall restores the colleague's view.
        Assert.Contains("open to the whole tenant", await partnerMatters.OpenMatterAccess("Confidential merger"));
        using (var colleague = await UserScopeAsync("it-colleague"))
        {
            var tools = colleague.ServiceProvider.GetRequiredService<MatterTools>();
            Assert.Contains("Confidential merger", await tools.ListMatters());
        }
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static async Task AttachAsync(
        IServiceScope scope, MatterTools matters, string matterName, string fileName, string content)
    {
        var files = scope.ServiceProvider.GetRequiredService<IFileStore>();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var stored = await files.SaveAsync(fileName, "text/plain", stream, source: "upload");
        await matters.AttachDocumentToMatter(stored.Id.ToString(), matterName);
    }

    /// <summary>Starts the ingest job through the tool and waits for the hosted processor to finish it.</summary>
    private async Task IndexAndWaitAsync(MatterTools matters, string matterName, string subject)
    {
        var started = await matters.IndexMatterDocuments(matterName);
        Assert.Contains("Job id:", started);
        var jobId = Guid.Parse(started.Split("Job id:")[1].Split(' ')[1].Trim());

        BackgroundJob? job = null;
        for (var i = 0; i < 120; i++)
        {
            using var pollScope = await UserScopeAsync(subject);
            job = await pollScope.ServiceProvider.GetRequiredService<IJobQueue>().FindAsync(jobId);
            if (job?.Status is JobStatus.Succeeded or JobStatus.Failed)
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.True(job?.Status == JobStatus.Succeeded, $"ingest job did not succeed: {job?.Status} — {job?.Error}");
    }

    private async Task<int> CountChunksAsync(string collectionName)
    {
        using var scope = await UserScopeAsync("it-user");
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var collection = await db.RagCollections.FirstAsync(c => c.Name == collectionName);
        return await db.RagChunks.CountAsync(c => c.CollectionId == collection.Id);
    }

    /// <summary>A scope acting as a JIT-provisioned dev-tenant user with wildcard permissions.</summary>
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

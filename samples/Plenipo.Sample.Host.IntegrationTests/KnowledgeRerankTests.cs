using Plenipo.Application.Files;
using Plenipo.Application.Rag;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Reranking against real Postgres and real (mock, but genuine) embeddings. The behaviour under
/// test is the one that matters at corpus scale: when the same clause appears in many documents,
/// the answer window must not fill up with copies of it.
/// </summary>
[Collection("api")]
public sealed class KnowledgeRerankTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Near_duplicate_boilerplate_does_not_crowd_out_the_distinct_answer()
    {
        using var scope = await UserScopeAsync("rerank-user");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var collectionId = await rag.GetOrCreateCollectionAsync(
            "knowledge", null, null, "rerank: contracts", language: "english");

        // Five contracts carrying the SAME confidentiality clause — the realistic case, where a
        // firm's templates repeat verbatim across a matter — plus one document that actually
        // answers a different question about the same subject.
        const string Boilerplate =
            "Each party shall keep confidential all information disclosed by the other party under " +
            "this agreement and shall not disclose it to any third party without prior written consent.";
        for (var i = 1; i <= 5; i++)
        {
            await IngestAsync(scope, rag, collectionId, $"contract-{i}.txt", Boilerplate);
        }

        await IngestAsync(scope, rag, collectionId, "carve-out.txt",
            "Confidential information does not include material that becomes public through no fault " +
            "of the receiving party, or that was already known to it before disclosure.");

        // Two slots. Without reranking they would both be copies of the same clause.
        var hits = await rag.SearchAsync("confidential information obligations", topK: 2);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.FileName == "carve-out.txt");

        var boilerplateHits = hits.Count(h => h.FileName.StartsWith("contract-", StringComparison.Ordinal));
        Assert.True(boilerplateHits <= 1, $"the window held {boilerplateHits} near-duplicate passages");
    }

    [Fact]
    public async Task Reranking_never_widens_what_retrieval_allowed()
    {
        // The safety property: reranking runs after every access check, so a deeper candidate pool
        // cannot surface a collection the caller is not scoped to. Same corpus, narrowed agent.
        using var owner = await UserScopeAsync("rerank-user");
        var ownerRag = owner.ServiceProvider.GetRequiredService<IRagService>();

        var visible = await ownerRag.GetOrCreateCollectionAsync("knowledge", null, null, "rerank: visible", language: "english");
        var hidden = await ownerRag.GetOrCreateCollectionAsync("knowledge", null, null, "rerank: hidden", language: "english");
        await IngestAsync(owner, ownerRag, visible, "visible.txt",
            "The escalation path for a severity one incident runs through the duty manager.");
        await IngestAsync(owner, ownerRag, hidden, "hidden.txt",
            "The escalation path for a severity one incident runs through the executive sponsor.");

        using var scoped = await UserScopeAsync("rerank-user", collectionScopes: ["knowledge/-/rerank: visible"]);
        var hits = await scoped.ServiceProvider.GetRequiredService<IRagService>()
            .SearchAsync("escalation path for a severity one incident", topK: 10);

        Assert.Contains(hits, h => h.FileName == "visible.txt");
        Assert.DoesNotContain(hits, h => h.FileName == "hidden.txt");
    }

    [Fact]
    public async Task The_most_relevant_passage_still_comes_first()
    {
        // Diversity must not cost the top hit. An exact identifier is the sharpest possible signal:
        // whatever reranking does afterwards, the passage containing it must lead.
        using var scope = await UserScopeAsync("rerank-user");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var collectionId = await rag.GetOrCreateCollectionAsync(
            "knowledge", null, null, "rerank: identifiers", language: "english");

        await IngestAsync(scope, rag, collectionId, "target.txt",
            "The indemnity schedule is recorded under reference QX-8842 and governs all claims.");
        for (var i = 1; i <= 4; i++)
        {
            await IngestAsync(scope, rag, collectionId, $"filler-{i}.txt",
                "General provisions regarding claims, notices, and the conduct of the parties under the agreement.");
        }

        var hits = await rag.SearchAsync("QX-8842", topK: 3);

        Assert.NotEmpty(hits);
        Assert.Equal("target.txt", hits[0].FileName);
    }

    // --- helpers ---------------------------------------------------------------------------------

    private static async Task IngestAsync(
        IServiceScope scope, IRagService rag, Guid collectionId, string fileName, string content)
    {
        var files = scope.ServiceProvider.GetRequiredService<IFileStore>();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var stored = await files.SaveAsync(fileName, "text/plain", stream, source: "upload");
        Assert.True(await rag.IngestFileAsync(collectionId, stored.Id) > 0, $"{fileName} produced no chunks");
    }

    private async Task<IServiceScope> UserScopeAsync(string subject, IReadOnlyList<string>? collectionScopes = null)
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

        if (collectionScopes is not null)
        {
            scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.AgentExecutionContext>()
                .SetCollectionScopes(collectionScopes);
        }

        return scope;
    }
}

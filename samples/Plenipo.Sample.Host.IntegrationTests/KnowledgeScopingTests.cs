using Plenipo.Application.Files;
using Plenipo.Application.Rag;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Context;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Infrastructure.Rag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The three narrowing layers added on top of collection gating, on real Postgres + pgvector:
/// per-chunk principal trimming (a restricted document inside a shared corpus), metadata facet
/// filters (the "same platform, any jurisdiction" requirement), and the agent's own collection
/// scope. Each is verified to NARROW and never to widen — every test asserts both the deny and the
/// allow side, because a filter that denies everything would pass a one-sided test.
/// </summary>
[Collection("api")]
public sealed class KnowledgeScopingTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Chunk_principals_trim_within_a_collection_the_caller_can_otherwise_see()
    {
        using var scope = await UserScopeAsync("scoping-owner");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var collectionId = await rag.GetOrCreateCollectionAsync(
            "knowledge", null, null, "acl: partner memos", language: "english");

        // Two documents in ONE collection: one unrestricted, one visible only to a principal the
        // caller does not hold. The collection gate admits both — the chunk ACL is what separates them.
        await IngestAsync(scope, rag, collectionId, "public-note.txt",
            "The quarterly billing rate for associates was raised to four hundred dollars per hour.");
        await IngestAsync(scope, rag, collectionId, "partner-only.txt",
            "The partnership compensation pool for the quarterly cycle totals nine million dollars.",
            new RagIngestOptions { Principals = [RagPrincipals.Role("equity-partner")] });

        var unrestricted = await rag.SearchAsync("quarterly compensation and billing");
        Assert.Contains(unrestricted, h => h.FileName == "public-note.txt");
        Assert.DoesNotContain(unrestricted, h => h.FileName == "partner-only.txt");

        // Holding the principal reveals it — the ACL narrows, it does not simply hide everything.
        using var partner = await UserScopeAsync("scoping-partner", principals: [RagPrincipals.Role("equity-partner")]);
        var partnerHits = await partner.ServiceProvider.GetRequiredService<IRagService>()
            .SearchAsync("quarterly compensation and billing");
        Assert.Contains(partnerHits, h => h.FileName == "partner-only.txt");
        Assert.Contains(partnerHits, h => h.FileName == "public-note.txt");
    }

    [Fact]
    public async Task Metadata_filters_select_by_facet_inside_a_shared_corpus()
    {
        using var scope = await UserScopeAsync("scoping-owner");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var collectionId = await rag.GetOrCreateCollectionAsync(
            "knowledge", null, null, "statutes: dismissal", language: "english");

        // The casewell shape: one library, many jurisdictions, answers must not cross borders.
        await IngestAsync(scope, rag, collectionId, "es-dismissal.txt",
            "Unfair dismissal entitles the employee to thirty-three days of salary per year worked.",
            new RagIngestOptions { Metadata = new Dictionary<string, string> { ["jurisdiction"] = "ES" } });
        await IngestAsync(scope, rag, collectionId, "de-dismissal.txt",
            "Unfair dismissal protection applies once the establishment employs more than ten people.",
            new RagIngestOptions { Metadata = new Dictionary<string, string> { ["jurisdiction"] = "DE" } });

        var spanish = await rag.SearchAsync("unfair dismissal", filters: new Dictionary<string, string> { ["jurisdiction"] = "ES" });
        Assert.Contains(spanish, h => h.FileName == "es-dismissal.txt");
        Assert.DoesNotContain(spanish, h => h.FileName == "de-dismissal.txt");

        var german = await rag.SearchAsync("unfair dismissal", filters: new Dictionary<string, string> { ["jurisdiction"] = "DE" });
        Assert.Contains(german, h => h.FileName == "de-dismissal.txt");
        Assert.DoesNotContain(german, h => h.FileName == "es-dismissal.txt");

        // No filter still spans both: filtering is opt-in, not a default narrowing.
        var both = await rag.SearchAsync("unfair dismissal");
        Assert.Contains(both, h => h.FileName == "es-dismissal.txt");
        Assert.Contains(both, h => h.FileName == "de-dismissal.txt");

        // An unmatched facet returns nothing rather than falling back to everything.
        var none = await rag.SearchAsync("unfair dismissal", filters: new Dictionary<string, string> { ["jurisdiction"] = "JP" });
        Assert.Empty(none);
    }

    [Fact]
    public async Task An_agents_collection_scope_narrows_retrieval_by_policy()
    {
        using var scope = await UserScopeAsync("scoping-owner");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();

        var spanish = await rag.GetOrCreateCollectionAsync("knowledge", null, null, "ES employment law", language: "english");
        var german = await rag.GetOrCreateCollectionAsync("knowledge", null, null, "DE employment law", language: "english");
        await IngestAsync(scope, rag, spanish, "es-notice.txt",
            "The statutory notice period for termination of an indefinite contract is fifteen days.");
        await IngestAsync(scope, rag, german, "de-notice.txt",
            "The statutory notice period for termination grows with the length of service, up to seven months.");

        // No scope: the user reaches both corpora.
        var unscoped = await rag.SearchAsync("statutory notice period for termination");
        Assert.Contains(unscoped, h => h.FileName == "es-notice.txt");
        Assert.Contains(unscoped, h => h.FileName == "de-notice.txt");

        // Scoped agent: same user, same permissions, but the agent is bound to the Spanish library.
        // This is the property that matters — the model cannot opt out of it by choosing arguments.
        using var scoped = await UserScopeAsync("scoping-owner", collectionScopes: ["knowledge/-/ES *"]);
        var scopedRag = scoped.ServiceProvider.GetRequiredService<IRagService>();

        var hits = await scopedRag.SearchAsync("statutory notice period for termination");
        Assert.Contains(hits, h => h.FileName == "es-notice.txt");
        Assert.DoesNotContain(hits, h => h.FileName == "de-notice.txt");

        // Naming the out-of-scope collection explicitly does not escape the scope.
        Assert.Empty(await scopedRag.SearchAsync("statutory notice period", collectionName: "DE employment law"));

        // And it is invisible to enumeration too, so the agent is not told what it cannot reach.
        var listed = await scopedRag.ListCollectionsAsync();
        Assert.Contains(listed, c => c.Name == "ES employment law");
        Assert.DoesNotContain(listed, c => c.Name == "DE employment law");
    }

    [Fact]
    public async Task Non_english_documents_are_retrievable_by_their_own_keywords()
    {
        using var scope = await UserScopeAsync("scoping-owner");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var collectionId = await rag.GetOrCreateCollectionAsync(
            "knowledge", null, null, "es: contratos", language: "spanish");

        await IngestAsync(scope, rag, collectionId, "contrato-es.txt",
            "Las partes acuerdan que el contrato podrá resolverse mediante un preaviso de treinta días. " +
            "La indemnización por despido improcedente se calculará conforme a la legislación vigente y " +
            "cualquier controversia se resolverá mediante arbitraje según la ley aplicable.");

        var stored = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().RagChunks
            .Where(c => c.CollectionId == collectionId)
            .Select(c => c.Language)
            .Distinct()
            .ToListAsync();
        Assert.Equal(["spanish"], stored);

        // Spanish stemming is the point: "despido" must find the passage that says "despido", and
        // an inflected query ("indemnizaciones") must reach the singular in the text. Under the old
        // English-only configuration neither stems correctly.
        var hits = await rag.SearchAsync("indemnizaciones por despido improcedente");
        Assert.Contains(hits, h => h.FileName == "contrato-es.txt");
    }

    [Fact]
    public async Task Collections_report_their_filter_keys_so_an_agent_can_discover_them()
    {
        using var scope = await UserScopeAsync("scoping-owner");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var collectionId = await rag.GetOrCreateCollectionAsync(
            "knowledge", null, null, "discovery: policies", language: "english",
            metadata: new Dictionary<string, string> { ["owner"] = "legal-ops" });

        await IngestAsync(scope, rag, collectionId, "policy.txt",
            "Records are retained for seven years and then destroyed under the retention schedule.",
            new RagIngestOptions
            {
                Metadata = new Dictionary<string, string> { ["jurisdiction"] = "EU", ["docType"] = "policy" },
            });

        var listed = await rag.ListCollectionsAsync();
        var collection = Assert.Single(listed, c => c.Id == collectionId);

        Assert.Equal(1, collection.DocumentCount);
        Assert.True(collection.ChunkCount > 0);
        Assert.Equal("legal-ops", collection.Metadata["owner"]);
        Assert.Equal(["docType", "jurisdiction"], collection.FilterKeys);

        // The agent-facing tool surfaces the same thing, which is what stops it guessing filter keys.
        var described = await scope.ServiceProvider.GetRequiredService<RagTools>().ListKnowledgeCollections();
        Assert.Contains("discovery: policies", described);
        Assert.Contains("jurisdiction", described);
    }

    [Fact]
    public async Task The_search_tool_parses_filters_and_reports_why_nothing_matched()
    {
        using var scope = await UserScopeAsync("scoping-owner");
        var rag = scope.ServiceProvider.GetRequiredService<IRagService>();
        var collectionId = await rag.GetOrCreateCollectionAsync(
            "knowledge", null, null, "tooling: handbook", language: "english");
        await IngestAsync(scope, rag, collectionId, "handbook.txt",
            "Expenses over five hundred dollars require prior written approval from a director.",
            new RagIngestOptions { Metadata = new Dictionary<string, string> { ["region"] = "EMEA" } });

        var tools = scope.ServiceProvider.GetRequiredService<RagTools>();

        var matched = await tools.SearchKnowledge("expense approval threshold", filters: "region=EMEA");
        Assert.Contains("handbook.txt", matched);

        // A filter that excludes everything must say so — and point at the recovery — rather than
        // looking like an empty corpus.
        var excluded = await tools.SearchKnowledge("expense approval threshold", filters: "region=APAC");
        Assert.Contains("region=APAC", excluded);
        Assert.Contains("list_knowledge_collections", excluded);
    }

    /// <summary>
    /// The RLS backstop, verified rather than assumed.
    /// <para>
    /// IMPORTANT: PostgreSQL superusers bypass row-level security entirely — <c>FORCE</c> included —
    /// and both this test container and many small deployments connect as one. So the test proves
    /// the property the way production gets it: it drops to a non-superuser role and shows the
    /// policy denying a cross-tenant read AND write there. If this ever regresses, the deployment
    /// guidance in docs/CONFIGURATION.md is the thing that stopped being true.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Row_level_security_isolates_tenants_for_a_non_superuser_connection()
    {
        using var scope = await UserScopeAsync("scoping-owner");
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var dev = await db.Tenants.FirstAsync(t => t.Slug == "dev");

        // 1. The session tenant reaches the database at all — without this the policies are inert.
        var sessionTenant = await db.Database
            .SqlQuery<Guid?>($"SELECT platform.current_tenant() AS \"Value\"")
            .SingleAsync();
        Assert.Equal(dev.Id, sessionTenant);

        // 2. The tables really are protected, and forced (so an owner connection is subject too).
        var forced = await db.Database.SqlQuery<bool>($"""
            SELECT relforcerowsecurity AS "Value" FROM pg_class
            WHERE oid = 'platform.rag_chunks'::regclass
            """).SingleAsync();
        Assert.True(forced, "rag_chunks does not have FORCE ROW LEVEL SECURITY");

        var policies = await db.Database.SqlQuery<string>($"""
            SELECT policyname AS "Value" FROM pg_policies
            WHERE schemaname = 'platform' AND tablename IN ('rag_chunks', 'rag_collections')
            ORDER BY policyname
            """).ToListAsync();
        Assert.Equal(["rag_chunks_tenant_isolation", "rag_collections_tenant_isolation"], policies);

        // 3. The policy actually denies, as a non-superuser. Seed a foreign tenant's row first (as
        //    the superuser the tests run as), then drop privileges and try to reach it.
        var foreignTenant = Guid.CreateVersion7();
        var foreignCollection = Guid.CreateVersion7();
        // IndexedLanguages and metadata are omitted deliberately — they carry database defaults, and
        // writing '{}' literals inside an interpolated raw string is more trouble than it is worth.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO platform.rag_collections
                ("Id", "TenantId", "ModuleId", "Name", "EmbeddingModel", "Language", "CreatedAt")
            VALUES ({foreignCollection}, {foreignTenant}, 'knowledge', 'other tenant corpus', 'mock-bow-384',
                    'english', now())
            """);

        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                DROP ROLE IF EXISTS rls_probe;
                CREATE ROLE rls_probe NOLOGIN;
                GRANT USAGE ON SCHEMA platform TO rls_probe;
                GRANT SELECT, INSERT ON platform.rag_collections TO rls_probe;
                """);

            // Control: the superuser connection this test runs on DOES see the foreign row, so the
            // next assertion is measuring the policy and not a failed insert.
            var visible = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM platform.rag_collections WHERE "Id" = {foreignCollection}
                """).SingleAsync();
            Assert.Equal(1, visible);

            var deniedRead = await ProbeAsync(db, $"""
                SELECT count(*)::int FROM platform.rag_collections WHERE "Id" = '{foreignCollection}'
                """);
            Assert.Equal("0", deniedRead);

            // And a write for another tenant is refused outright.
            var deniedWrite = await ProbeAsync(db, $"""
                INSERT INTO platform.rag_collections
                    ("Id", "TenantId", "ModuleId", "Name", "EmbeddingModel", "Language", "CreatedAt")
                VALUES ('{Guid.CreateVersion7()}', '{Guid.CreateVersion7()}', 'knowledge', 'smuggled',
                        'mock-bow-384', 'english', now())
                """);
            Assert.Contains("row-level security", deniedWrite, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM platform.rag_collections WHERE \"Id\" = {foreignCollection}");
            await db.Database.ExecuteSqlRawAsync("""
                REVOKE ALL ON platform.rag_collections FROM rls_probe;
                REVOKE USAGE ON SCHEMA platform FROM rls_probe;
                DROP ROLE IF EXISTS rls_probe;
                """);
        }
    }

    // --- helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// Runs one statement as the unprivileged <c>rls_probe</c> role inside a rolled-back
    /// transaction, returning its scalar result or the error text. <c>SET LOCAL ROLE</c> means the
    /// privilege drop cannot leak past the statement even if it throws.
    /// </summary>
    private static async Task<string> ProbeAsync(PlatformDbContext db, string statement)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var setup = connection.CreateCommand())
            {
                setup.Transaction = transaction;
                setup.CommandText =
                    "SELECT set_config('plenipo.tenant_id', (SELECT \"Id\"::text FROM platform.tenants WHERE \"Slug\" = 'dev'), true); " +
                    "SET LOCAL ROLE rls_probe;";
                await setup.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            var scalar = await command.ExecuteScalarAsync();
            return scalar?.ToString() ?? "null";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }


    private static async Task IngestAsync(
        IServiceScope scope, IRagService rag, Guid collectionId, string fileName, string content,
        RagIngestOptions? options = null)
    {
        var files = scope.ServiceProvider.GetRequiredService<IFileStore>();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var stored = await files.SaveAsync(fileName, "text/plain", stream, source: "upload");
        var chunks = await rag.IngestFileAsync(collectionId, stored.Id, options);
        Assert.True(chunks > 0, $"{fileName} produced no chunks");
    }

    /// <summary>
    /// A scope acting as a dev-tenant user, optionally holding extra retrieval principals or running
    /// under an agent whose knowledge scope is narrowed.
    /// </summary>
    private async Task<IServiceScope> UserScopeAsync(
        string subject, IReadOnlyList<string>? principals = null, IReadOnlyList<string>? collectionScopes = null)
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
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();

        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == subject);
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);

        if (collectionScopes is not null)
        {
            scope.ServiceProvider.GetRequiredService<AgentExecutionContext>().SetCollectionScopes(collectionScopes);
        }

        if (principals is not null)
        {
            // Grant the principals as real tenant roles, so the default resolver reports them —
            // the test exercises the shipped resolver rather than a stand-in.
            foreach (var principal in principals)
            {
                var role = principal.StartsWith("role:", StringComparison.Ordinal) ? principal[5..] : principal;
                if (!await db.UserRoles.AnyAsync(r => r.UserId == user.Id && r.Role == role))
                {
                    db.UserRoles.Add(new UserRole { TenantId = tenant.Id, UserId = user.Id, Role = role });
                }
            }

            await db.SaveChangesAsync();
        }

        return scope;
    }
}

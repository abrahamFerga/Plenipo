using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Plenipo.Application.Ai;
using Plenipo.Application.Documents;
using Plenipo.Application.Files;
using Plenipo.Application.Rag;
using Plenipo.Core.Multitenancy;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// The RAG pipeline over the platform database: pgvector + tsvector in the same Postgres, no extra
/// service. Retrieval is hybrid — a vector arm and a full-text arm, each carrying EVERY narrowing
/// predicate (tenant, allowed collections, chunk ACL, metadata facets) so nothing is filtered after
/// fusion, merged with reciprocal rank fusion (rank-based, because cosine and ts_rank scores are not
/// on comparable scales). Access resolves in three layers and FAILS CLOSED at each: the agent's
/// collection scope narrows, the module's <see cref="IRagCollectionGate"/> decides the collection,
/// and per-chunk principals trim within it — then the final hits are re-checked. The lexical arm is
/// language-aware: one constant <c>plainto_tsquery</c> per configuration present in scope, which
/// keeps the GIN index usable where a per-row <c>regconfig</c> would not.
/// <para>
/// Retrieval is recall-oriented and ranking is a separate, later step: the query fetches the deeper
/// shortlist the configured <see cref="IRagReranker"/> asked for, and the reranker cuts it to the
/// requested depth AFTER every access check has run. Reranking therefore only ever re-orders an
/// already-authorised set.
/// </para>
/// </summary>
public sealed class RagService(
    PlatformDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IDocumentReader reader,
    IFileStore files,
    ITenantContext tenant,
    IEnumerable<IRagCollectionGate> gates,
    IRagPrincipalResolver principals,
    IAgentExecutionContext agentContext,
    IRagReranker reranker,
    IOptions<RagOptions> ragOptions) : IRagService
{
    /// <summary>Standard RRF dampening constant — rank 1 in one arm scores 1/61.</summary>
    private const int RrfK = 60;

    /// <summary>Depth each arm feeds into fusion.</summary>
    private const int ArmLimit = 50;

    /// <summary>Embeddings written per round trip — see <see cref="StampEmbeddingsAsync"/>.</summary>
    private const int EmbeddingBatchSize = 200;

    public async Task<Guid> GetOrCreateCollectionAsync(
        string moduleId, string? resourceType, Guid? resourceId, string name,
        string? language = null, IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var existing = resourceType is not null
            ? await db.RagCollections.FirstOrDefaultAsync(
                c => c.ModuleId == moduleId && c.ResourceType == resourceType && c.ResourceId == resourceId,
                cancellationToken)
            : await db.RagCollections.FirstOrDefaultAsync(
                c => c.ModuleId == moduleId && c.ResourceType == null && EF.Functions.ILike(c.Name, name),
                cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var collection = new RagCollection
        {
            TenantId = tenant.RequireTenantId(),
            ModuleId = moduleId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Name = name,
            EmbeddingModel = ragOptions.Value.EmbeddingModel,
            Language = RagLanguage.Normalize(language ?? ragOptions.Value.DefaultLanguage),
            Metadata = metadata is null ? [] : new Dictionary<string, string>(metadata, StringComparer.Ordinal),
        };
        db.RagCollections.Add(collection);
        await db.SaveChangesAsync(cancellationToken);
        return collection.Id;
    }

    public async Task<int> IngestFileAsync(
        Guid collectionId, Guid fileId, RagIngestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Tenant-scoped lookups: a foreign tenant's ids behave like missing ones.
        var collection = await db.RagCollections.FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken)
            ?? throw new InvalidOperationException($"RAG collection {collectionId} does not exist.");
        var file = await files.FindAsync(fileId, cancellationToken)
            ?? throw new InvalidOperationException($"Stored file {fileId} does not exist.");

        // Page-aware extraction: the boundaries come back with the text so a passage can cite its page.
        var extracted = await reader.ExtractAsync(fileId, cancellationToken);
        var text = extracted?.Text;

        // Idempotent re-ingest: replace whatever this file contributed before.
        await db.RagChunks
            .Where(c => c.CollectionId == collection.Id && c.FileId == fileId)
            .ExecuteDeleteAsync(cancellationToken);

        if (extracted is null || string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var language = LanguageDetector.Detect(text, options?.Language, collection.Language);
        var chunkPrincipals = options?.Principals?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList() ?? [];
        var chunkMetadata = options?.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(options.Metadata, StringComparer.Ordinal);

        var pieces = TextChunker.Chunk(text, ragOptions.Value.MaxChunkChars);
        var embeddings = await embedder.GenerateAsync(pieces.Select(p => p.Text).ToList(), cancellationToken: cancellationToken);

        var chunks = pieces.Select((piece, i) =>
        {
            // The chunk is a contiguous slice, so its offsets map cleanly onto the page ranges the
            // extractor reported. Unpaginated sources yield (null, null) and cite the file alone.
            var (pageFrom, pageTo) = extracted.PagesFor(piece.Start, piece.End);
            return new RagChunk
            {
                TenantId = collection.TenantId,
                CollectionId = collection.Id,
                FileId = fileId,
                FileName = file.FileName,
                Ordinal = i,
                Text = piece.Text,
                EmbeddingModel = ragOptions.Value.EmbeddingModel,
                ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(piece.Text))),
                Language = language,
                Principals = chunkPrincipals,
                Metadata = chunkMetadata,
                PageFrom = pageFrom,
                PageTo = pageTo,
            };
        }).ToList();

        db.RagChunks.AddRange(chunks);
        await db.SaveChangesAsync(cancellationToken);

        await StampEmbeddingsAsync(chunks, embeddings, language, cancellationToken);
        await TrackIndexedLanguageAsync(collection, language, cancellationToken);

        return chunks.Count;
    }

    /// <summary>
    /// Writes the vector and the language-correct <c>tsv</c> for a document's chunks. Both columns
    /// are SQL-only (see <see cref="RagChunk"/>), and both are written here in batches via
    /// <c>unnest</c> — a case with thousands of documents makes per-chunk round trips the dominant
    /// cost of ingestion, and a single statement per few hundred chunks removes it.
    /// </summary>
    private async Task StampEmbeddingsAsync(
        List<RagChunk> chunks, GeneratedEmbeddings<Embedding<float>> embeddings, string language,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < chunks.Count; offset += EmbeddingBatchSize)
        {
            var batch = chunks.Skip(offset).Take(EmbeddingBatchSize).ToList();
            var ids = batch.Select(c => c.Id).ToArray();
            var vectors = batch.Select((_, i) => ToVectorLiteral(embeddings[offset + i].Vector.Span)).ToArray();

            // `language` is never interpolated: RagLanguage.Normalize has already reduced it to a
            // known configuration name, and it travels as a parameter cast to regconfig.
            await db.Database.ExecuteSqlAsync(
                $"""
                 UPDATE platform.rag_chunks AS c
                 SET embedding = CAST(src.vec AS vector),
                     tsv = to_tsvector(CAST({language} AS regconfig), c."Text")
                 FROM (SELECT * FROM unnest({ids}, {vectors}) AS t(id, vec)) AS src
                 WHERE c."Id" = src.id
                 """,
                cancellationToken);
        }
    }

    /// <summary>
    /// Records that this collection now contains a configuration, so retrieval knows which
    /// <c>plainto_tsquery</c> constants to build without scanning the chunks to find out.
    /// </summary>
    private async Task TrackIndexedLanguageAsync(RagCollection collection, string language, CancellationToken cancellationToken)
    {
        if (collection.IndexedLanguages.Contains(language, StringComparer.Ordinal))
        {
            return;
        }

        collection.IndexedLanguages = [.. collection.IndexedLanguages, language];
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RagHit>> SearchAsync(
        string query, string? collectionName = null, int? topK = null,
        IReadOnlyDictionary<string, string>? filters = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var allowed = await ResolveAccessibleCollectionsAsync(collectionName, cancellationToken);
        if (allowed.Count == 0)
        {
            return [];
        }

        var tenantId = tenant.RequireTenantId();
        var allowedIds = allowed.Keys.ToArray();
        var model = ragOptions.Value.EmbeddingModel;
        var top = Math.Clamp(topK ?? ragOptions.Value.TopK, 1, 50);

        // Retrieval fetches the shortlist the reranker asked for, then the reranker cuts it to
        // `top`. Both arms are widened to match, or asking for a deeper pool would just hit the
        // arms' own ceiling and return the same candidates.
        var candidateCount = Math.Clamp(reranker.CandidateCountFor(top), top, RagOptions.MaxRerankCandidates);
        var armLimit = Math.Max(ArmLimit, candidateCount);

        // The caller's principals, and the languages actually present in the collections in scope.
        var callerPrincipals = (await principals.GetPrincipalsAsync(cancellationToken)).ToArray();
        var languages = allowed.Values
            .SelectMany(c => c.Languages)
            .Distinct(StringComparer.Ordinal)
            .DefaultIfEmpty(RagLanguage.Default)
            .ToArray();
        var filterJson = ToFilterJson(filters);

        var queryEmbedding = await embedder.GenerateAsync([query], cancellationToken: cancellationToken);
        var queryVector = ToVectorLiteral(queryEmbedding[0].Vector.Span);

        // Both arms carry the tenant, collection, ACL and metadata predicates; fusion never widens
        // access. The vector arm additionally pins the embedding model — vectors from a different
        // model are not comparable and would poison the ranking during a re-embed migration. The
        // lexical arm joins one constant tsquery per language in scope, so the GIN index applies.
        var ranked = await db.Database.SqlQuery<RankedChunk>($"""
            WITH vec AS (
                SELECT c."Id" AS id, ROW_NUMBER() OVER (ORDER BY c.embedding <=> CAST({queryVector} AS vector)) AS rank
                FROM platform.rag_chunks c
                WHERE c."TenantId" = {tenantId}
                  AND c."CollectionId" = ANY({allowedIds})
                  AND c.embedding IS NOT NULL
                  AND c."EmbeddingModel" = {model}
                  AND (cardinality(c."Principals") = 0 OR c."Principals" && {callerPrincipals})
                  -- Cast before the null test: a bare parameter placeholder gives Postgres nothing
                  -- to infer a type from, and "$n IS NULL" alone fails to plan.
                  AND (CAST({filterJson} AS jsonb) IS NULL OR c.metadata @> CAST({filterJson} AS jsonb))
                ORDER BY c.embedding <=> CAST({queryVector} AS vector)
                LIMIT {armLimit}
            ),
            lex AS (
                SELECT c."Id" AS id,
                       ROW_NUMBER() OVER (ORDER BY ts_rank_cd(c.tsv, plainto_tsquery(CAST(cfg AS regconfig), {query})) DESC) AS rank
                FROM platform.rag_chunks c
                -- One row per chunk: a chunk has exactly one language, so this joins 1:1 and binds
                -- cfg per iteration, which keeps the tsquery constant for the inner index scan.
                JOIN unnest({languages}) AS cfg ON cfg = c."Language"
                WHERE c."TenantId" = {tenantId}
                  AND c."CollectionId" = ANY({allowedIds})
                  AND c.tsv @@ plainto_tsquery(CAST(cfg AS regconfig), {query})
                  AND (cardinality(c."Principals") = 0 OR c."Principals" && {callerPrincipals})
                  -- Cast before the null test: a bare parameter placeholder gives Postgres nothing
                  -- to infer a type from, and "$n IS NULL" alone fails to plan.
                  AND (CAST({filterJson} AS jsonb) IS NULL OR c.metadata @> CAST({filterJson} AS jsonb))
                ORDER BY rank
                LIMIT {armLimit}
            )
            SELECT COALESCE(vec.id, lex.id) AS "Id",
                   CAST(COALESCE(1.0 / ({RrfK} + vec.rank), 0) + COALESCE(1.0 / ({RrfK} + lex.rank), 0) AS double precision) AS "Score"
            FROM vec FULL OUTER JOIN lex ON vec.id = lex.id
            ORDER BY "Score" DESC
            LIMIT {candidateCount}
            """).ToListAsync(cancellationToken);

        if (ranked.Count == 0)
        {
            return [];
        }

        // Hydrate through EF (the tenant query filter applies again — defense in depth) and
        // fail-closed recheck: every hit's collection must still pass its gate right now, and the
        // chunk's own ACL is re-evaluated in managed code rather than trusted from the SQL arm.
        var ids = ranked.Select(r => r.Id).ToArray();
        var chunks = await db.RagChunks.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var callerSet = new HashSet<string>(callerPrincipals, StringComparer.Ordinal);
        var vectors = reranker.UsesEmbeddings
            ? await ReadEmbeddingsAsync(ids, cancellationToken)
            : [];

        var candidates = new List<RagCandidate>(ranked.Count);
        foreach (var row in ranked)
        {
            if (!chunks.TryGetValue(row.Id, out var chunk) ||
                !allowed.TryGetValue(chunk.CollectionId, out var collection) ||
                !IsPrincipalAllowed(chunk, callerSet) ||
                !await IsStillAllowedAsync(chunk.CollectionId, cancellationToken))
            {
                continue; // fail closed: unverifiable hits are dropped, never returned
            }

            var hit = new RagHit(
                chunk.Id, chunk.CollectionId, collection.Name, chunk.FileId, chunk.FileName,
                chunk.Ordinal, chunk.Text, row.Score, chunk.PageFrom, chunk.PageTo);
            candidates.Add(new RagCandidate(hit, row.Score, vectors.GetValueOrDefault(chunk.Id, [])));
        }

        // Rerank AFTER every access check, never before: the reranker only re-orders and truncates a
        // set that is already authorised, so it can never surface something the gates excluded.
        return await reranker.RerankAsync(query, candidates, top, cancellationToken);
    }

    /// <summary>
    /// Reads the candidates' vectors for a reranker that compares them. pgvector's text form is the
    /// same shape ingestion writes, so this needs no extra driver mapping; the set is a few dozen
    /// rows, already narrowed by every access predicate.
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlyList<float>>> ReadEmbeddingsAsync(
        Guid[] ids, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<EmbeddingRow>($"""
            SELECT "Id" AS "Id", embedding::text AS "Vector"
            FROM platform.rag_chunks
            WHERE "Id" = ANY({ids}) AND embedding IS NOT NULL
            """).ToListAsync(cancellationToken);

        var vectors = new Dictionary<Guid, IReadOnlyList<float>>(rows.Count);
        foreach (var row in rows)
        {
            var parsed = ParseVectorLiteral(row.Vector);
            if (parsed.Count > 0)
            {
                vectors[row.Id] = parsed;
            }
        }

        return vectors;
    }

    /// <summary>Parses pgvector's <c>[0.1,0.2,…]</c> text form. A malformed value yields nothing, never a throw.</summary>
    private static List<float> ParseVectorLiteral(string? literal)
    {
        if (string.IsNullOrWhiteSpace(literal))
        {
            return [];
        }

        var span = literal.AsSpan().Trim().Trim('[').Trim(']');
        if (span.IsEmpty)
        {
            return [];
        }

        var values = new List<float>(512);
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != ',')
            {
                continue;
            }

            if (!float.TryParse(span[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return []; // a value we cannot read makes the whole vector untrustworthy
            }

            values.Add(value);
            start = i + 1;
        }

        return values;
    }

    public async Task<IReadOnlyList<RagCollectionInfo>> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var allowed = await ResolveAccessibleCollectionsAsync(null, cancellationToken);
        if (allowed.Count == 0)
        {
            return [];
        }

        var ids = allowed.Keys.ToArray();
        var stats = await db.RagChunks
            .Where(c => ids.Contains(c.CollectionId))
            .GroupBy(c => c.CollectionId)
            .Select(g => new
            {
                CollectionId = g.Key,
                Chunks = g.Count(),
                Documents = g.Select(c => c.FileId).Distinct().Count(),
            })
            .ToListAsync(cancellationToken);
        var byCollection = stats.ToDictionary(s => s.CollectionId);

        // Filter keys are discovered from the corpus rather than declared: whatever a module or
        // connector stamped is what an agent can filter on, with no registry to keep in sync.
        var filterKeys = await db.RagChunks
            .Where(c => ids.Contains(c.CollectionId))
            .Select(c => new { c.CollectionId, c.Metadata })
            .Take(2000)
            .ToListAsync(cancellationToken);
        var keysByCollection = filterKeys
            .GroupBy(x => x.CollectionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.SelectMany(x => x.Metadata.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());

        return [.. allowed.Values
            .Select(c => new RagCollectionInfo(
                c.Id, c.ModuleId, c.ResourceType, c.ResourceId, c.Name, c.Language, c.EmbeddingModel,
                byCollection.GetValueOrDefault(c.Id)?.Documents ?? 0,
                byCollection.GetValueOrDefault(c.Id)?.Chunks ?? 0,
                c.Metadata,
                keysByCollection.GetValueOrDefault(c.Id, [])))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The collections the caller may query right now: tenant-scoped (EF filter), optionally
    /// narrowed by name, narrowed again by the active agent's collection scopes, then gated — an
    /// unbound collection is module-level (the tool permission suffices); a bound one needs its
    /// resource gate to exist AND allow.
    /// </summary>
    private async Task<Dictionary<Guid, AllowedCollection>> ResolveAccessibleCollectionsAsync(
        string? collectionName, CancellationToken cancellationToken)
    {
        var candidates = string.IsNullOrWhiteSpace(collectionName)
            ? await db.RagCollections.ToListAsync(cancellationToken)
            : await db.RagCollections.Where(c => EF.Functions.ILike(c.Name, collectionName.Trim())).ToListAsync(cancellationToken);

        var scopes = agentContext.CollectionScopes;
        var allowed = new Dictionary<Guid, AllowedCollection>();
        foreach (var collection in candidates)
        {
            if (!AgentCollectionSelection.Matches(scopes, collection.ModuleId, collection.ResourceType, collection.Name))
            {
                continue;
            }

            if (collection.ResourceType is null || await IsGateOpenAsync(collection, cancellationToken))
            {
                allowed[collection.Id] = AllowedCollection.From(collection);
            }
        }

        return allowed;
    }

    private async Task<bool> IsGateOpenAsync(RagCollection collection, CancellationToken cancellationToken)
    {
        var gate = gates.FirstOrDefault(g => string.Equals(g.ResourceType, collection.ResourceType, StringComparison.Ordinal));
        return gate is not null && collection.ResourceId is Guid resourceId &&
               await gate.CanQueryAsync(resourceId, cancellationToken);
    }

    private async Task<bool> IsStillAllowedAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        var collection = await db.RagCollections.FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken);
        if (collection is null)
        {
            return false;
        }

        return collection.ResourceType is null || await IsGateOpenAsync(collection, cancellationToken);
    }

    /// <summary>
    /// Chunk-level trimming: an empty principal list means "the collection gate already decided",
    /// a non-empty one must overlap the caller's principals. Restriction is therefore opt-in per
    /// document, while access to a restricted one is opt-in per principal.
    /// </summary>
    private static bool IsPrincipalAllowed(RagChunk chunk, HashSet<string> callerPrincipals) =>
        chunk.Principals.Count == 0 || chunk.Principals.Any(callerPrincipals.Contains);

    /// <summary>
    /// Metadata filters become one jsonb containment test, so <c>{"jurisdiction":"ES"}</c> matches a
    /// chunk carrying that pair among others. Null when there is nothing to filter — the SQL then
    /// short-circuits instead of comparing against an empty object (which matches everything but
    /// still costs a jsonb parse per row).
    /// </summary>
    private static string? ToFilterJson(IReadOnlyDictionary<string, string>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return null;
        }

        var usable = filters
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value ?? string.Empty, StringComparer.Ordinal);

        return usable.Count == 0 ? null : JsonSerializer.Serialize(usable, JsonSerializerOptions.Web);
    }

    private static string ToVectorLiteral(ReadOnlySpan<float> vector)
    {
        var sb = new StringBuilder(vector.Length * 10);
        sb.Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(vector[i].ToString(CultureInfo.InvariantCulture));
        }

        return sb.Append(']').ToString();
    }

    /// <summary>A collection that passed every gate, kept so hits can be labelled without a re-query.</summary>
    private sealed record AllowedCollection(
        Guid Id, string ModuleId, string? ResourceType, Guid? ResourceId, string Name,
        string Language, string EmbeddingModel, IReadOnlyList<string> Languages,
        IReadOnlyDictionary<string, string> Metadata)
    {
        public static AllowedCollection From(RagCollection c) => new(
            c.Id, c.ModuleId, c.ResourceType, c.ResourceId, c.Name, c.Language, c.EmbeddingModel,
            c.IndexedLanguages.Count > 0 ? c.IndexedLanguages : [c.Language],
            c.Metadata);
    }

    private sealed class RankedChunk
    {
        public Guid Id { get; set; }

        public double Score { get; set; }
    }

    /// <summary>A candidate's vector in pgvector's text form, for the reranker to compare.</summary>
    private sealed class EmbeddingRow
    {
        public Guid Id { get; set; }

        public string? Vector { get; set; }
    }
}

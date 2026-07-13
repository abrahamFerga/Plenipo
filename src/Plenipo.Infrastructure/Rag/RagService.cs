using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// service. Retrieval is hybrid — a vector arm and a full-text arm, each carrying the tenant and
/// allowed-collection predicates (never filtered after fusion), merged with reciprocal rank fusion
/// (rank-based, because cosine and ts_rank scores are not on comparable scales). Collection access
/// resolves through <see cref="IRagCollectionGate"/>s and FAILS CLOSED: a resource-bound collection
/// with no registered gate, or whose gate denies, is excluded before the query and re-checked on
/// the final hits. The embedding column is SQL-only (written via raw SQL) so the entity model works
/// on non-Postgres test providers; small collections use exact scan — perfect recall, no index.
/// </summary>
public sealed class RagService(
    PlatformDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    IDocumentReader reader,
    IFileStore files,
    ITenantContext tenant,
    IEnumerable<IRagCollectionGate> gates,
    IOptions<RagOptions> options) : IRagService
{
    /// <summary>Standard RRF dampening constant — rank 1 in one arm scores 1/61.</summary>
    private const int RrfK = 60;

    /// <summary>Depth each arm feeds into fusion.</summary>
    private const int ArmLimit = 50;

    public async Task<Guid> GetOrCreateCollectionAsync(
        string moduleId, string? resourceType, Guid? resourceId, string name,
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
            EmbeddingModel = options.Value.EmbeddingModel,
        };
        db.RagCollections.Add(collection);
        await db.SaveChangesAsync(cancellationToken);
        return collection.Id;
    }

    public async Task<int> IngestFileAsync(Guid collectionId, Guid fileId, CancellationToken cancellationToken = default)
    {
        // Tenant-scoped lookups: a foreign tenant's ids behave like missing ones.
        var collection = await db.RagCollections.FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken)
            ?? throw new InvalidOperationException($"RAG collection {collectionId} does not exist.");
        var file = await files.FindAsync(fileId, cancellationToken)
            ?? throw new InvalidOperationException($"Stored file {fileId} does not exist.");

        var text = await reader.ExtractTextAsync(fileId, cancellationToken);

        // Idempotent re-ingest: replace whatever this file contributed before.
        await db.RagChunks
            .Where(c => c.CollectionId == collection.Id && c.FileId == fileId)
            .ExecuteDeleteAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var pieces = TextChunker.Chunk(text, options.Value.MaxChunkChars);
        var embeddings = await embedder.GenerateAsync(pieces, cancellationToken: cancellationToken);

        var chunks = pieces.Select((piece, i) => new RagChunk
        {
            TenantId = collection.TenantId,
            CollectionId = collection.Id,
            FileId = fileId,
            FileName = file.FileName,
            Ordinal = i,
            Text = piece,
            EmbeddingModel = options.Value.EmbeddingModel,
            ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(piece))),
        }).ToList();

        db.RagChunks.AddRange(chunks);
        await db.SaveChangesAsync(cancellationToken);

        // The vector column is unmapped (see class doc) — stamp embeddings with raw SQL.
        for (var i = 0; i < chunks.Count; i++)
        {
            var literal = ToVectorLiteral(embeddings[i].Vector.Span);
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE platform.rag_chunks SET embedding = CAST({literal} AS vector) WHERE "Id" = {chunks[i].Id}""",
                cancellationToken);
        }

        return chunks.Count;
    }

    public async Task<IReadOnlyList<RagHit>> SearchAsync(
        string query, string? collectionName = null, int? topK = null,
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
        var model = options.Value.EmbeddingModel;
        var top = Math.Clamp(topK ?? options.Value.TopK, 1, 50);

        var queryEmbedding = await embedder.GenerateAsync([query], cancellationToken: cancellationToken);
        var queryVector = ToVectorLiteral(queryEmbedding[0].Vector.Span);

        // Both arms carry the tenant + allowed-collection predicates; fusion never widens access.
        // The vector arm additionally pins the embedding model — vectors from a different model are
        // not comparable and would poison the ranking during a re-embed migration.
        var ranked = await db.Database.SqlQuery<RankedChunk>($"""
            WITH vec AS (
                SELECT c."Id" AS id, ROW_NUMBER() OVER (ORDER BY c.embedding <=> CAST({queryVector} AS vector)) AS rank
                FROM platform.rag_chunks c
                WHERE c."TenantId" = {tenantId}
                  AND c."CollectionId" = ANY({allowedIds})
                  AND c.embedding IS NOT NULL
                  AND c."EmbeddingModel" = {model}
                ORDER BY c.embedding <=> CAST({queryVector} AS vector)
                LIMIT {ArmLimit}
            ),
            lex AS (
                SELECT c."Id" AS id, ROW_NUMBER() OVER (ORDER BY ts_rank_cd(c.tsv, plainto_tsquery('english', {query})) DESC) AS rank
                FROM platform.rag_chunks c
                WHERE c."TenantId" = {tenantId}
                  AND c."CollectionId" = ANY({allowedIds})
                  AND c.tsv @@ plainto_tsquery('english', {query})
                ORDER BY rank
                LIMIT {ArmLimit}
            )
            SELECT COALESCE(vec.id, lex.id) AS "Id",
                   CAST(COALESCE(1.0 / ({RrfK} + vec.rank), 0) + COALESCE(1.0 / ({RrfK} + lex.rank), 0) AS double precision) AS "Score"
            FROM vec FULL OUTER JOIN lex ON vec.id = lex.id
            ORDER BY "Score" DESC
            LIMIT {top}
            """).ToListAsync(cancellationToken);

        if (ranked.Count == 0)
        {
            return [];
        }

        // Hydrate through EF (the tenant query filter applies again — defense in depth) and
        // fail-closed recheck: every hit's collection must still pass its gate right now.
        var ids = ranked.Select(r => r.Id).ToArray();
        var chunks = await db.RagChunks.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);

        var hits = new List<RagHit>(ranked.Count);
        foreach (var row in ranked)
        {
            if (!chunks.TryGetValue(row.Id, out var chunk) ||
                !allowed.TryGetValue(chunk.CollectionId, out var name) ||
                !await IsStillAllowedAsync(chunk.CollectionId, cancellationToken))
            {
                continue; // fail closed: unverifiable hits are dropped, never returned
            }

            hits.Add(new RagHit(chunk.Id, chunk.CollectionId, name, chunk.FileId, chunk.FileName, chunk.Ordinal, chunk.Text, row.Score));
        }

        return hits;
    }

    /// <summary>
    /// The collections the caller may query right now: tenant-scoped (EF filter), optionally
    /// narrowed by name, then gated — an unbound collection is module-level (the tool permission
    /// suffices); a bound one needs its resource gate to exist AND allow.
    /// </summary>
    private async Task<Dictionary<Guid, string>> ResolveAccessibleCollectionsAsync(
        string? collectionName, CancellationToken cancellationToken)
    {
        var candidates = string.IsNullOrWhiteSpace(collectionName)
            ? await db.RagCollections.ToListAsync(cancellationToken)
            : await db.RagCollections.Where(c => EF.Functions.ILike(c.Name, collectionName.Trim())).ToListAsync(cancellationToken);

        var allowed = new Dictionary<Guid, string>();
        foreach (var collection in candidates)
        {
            if (collection.ResourceType is null)
            {
                allowed[collection.Id] = collection.Name;
                continue;
            }

            var gate = gates.FirstOrDefault(g => string.Equals(g.ResourceType, collection.ResourceType, StringComparison.Ordinal));
            if (gate is not null && collection.ResourceId is Guid resourceId &&
                await gate.CanQueryAsync(resourceId, cancellationToken))
            {
                allowed[collection.Id] = collection.Name;
            }
        }

        return allowed;
    }

    private async Task<bool> IsStillAllowedAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        var collection = await db.RagCollections.FirstOrDefaultAsync(c => c.Id == collectionId, cancellationToken);
        if (collection is null)
        {
            return false;
        }

        if (collection.ResourceType is null)
        {
            return true;
        }

        var gate = gates.FirstOrDefault(g => string.Equals(g.ResourceType, collection.ResourceType, StringComparison.Ordinal));
        return gate is not null && collection.ResourceId is Guid resourceId &&
               await gate.CanQueryAsync(resourceId, cancellationToken);
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

    private sealed class RankedChunk
    {
        public Guid Id { get; set; }

        public double Score { get; set; }
    }
}

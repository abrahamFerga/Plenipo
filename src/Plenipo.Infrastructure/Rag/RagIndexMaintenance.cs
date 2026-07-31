using System.Globalization;
using Plenipo.Application.Rag;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// Keeps the vector column indexable as a deployment grows. Small corpora deliberately have no
/// vector index — exact scan gives perfect recall for free, and that is the right trade while a
/// collection is a few thousand chunks. Past <see cref="RagOptions.IndexThresholdChunks"/> the scan
/// cost stops being free (a case with thousands of documents is tens of thousands of chunks), so
/// this promotes the table to HNSW once, idempotently.
/// <para>
/// Everything here is best-effort: an index is an optimisation, never a correctness requirement, so
/// a failure is logged and ingestion still succeeds. Retrieval is identical either way.
/// </para>
/// </summary>
public sealed class RagIndexMaintenance(
    PlatformDbContext db,
    IOptions<RagOptions> ragOptions,
    ILogger<RagIndexMaintenance> logger)
{
    private const string IndexName = "IX_rag_chunks_embedding_hnsw";

    /// <summary>
    /// Promotes <c>rag_chunks.embedding</c> to an HNSW index when the table has outgrown exact scan.
    /// The column ships dimensionless so that changing embedding model is a re-embed rather than a
    /// schema migration; HNSW needs a fixed dimension, so the first promotion pins the column to the
    /// dimension actually in use. A later model with different dimensions therefore needs the index
    /// dropped and the column re-typed — which is already what a re-embed migration does.
    /// </summary>
    public async Task EnsureVectorIndexAsync(CancellationToken cancellationToken = default)
    {
        var threshold = ragOptions.Value.IndexThresholdChunks;
        if (threshold <= 0)
        {
            return; // explicitly disabled
        }

        try
        {
            if (await IndexExistsAsync(cancellationToken))
            {
                return;
            }

            // Table-wide, deliberately: the index serves every tenant's chunks, and the query's
            // tenant/collection predicates are what narrow it.
            var total = await db.RagChunks.IgnoreQueryFilters().CountAsync(cancellationToken);
            if (total < threshold)
            {
                return;
            }

            var dimensions = await DimensionsAsync(cancellationToken);
            if (dimensions is not > 0)
            {
                return; // nothing embedded yet, or mixed models — leave exact scan in place
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Promoting rag_chunks to an HNSW index: {Chunks} chunks (threshold {Threshold}), {Dimensions} dimensions.",
                    total, threshold, dimensions.Value);
            }

            // DDL cannot be parameterised, so these are composed. Both interpolated values are safe
            // by construction and not by trust: the dimension is an int Postgres itself just
            // reported, and the index name is a compile-time constant.
            var alter = "ALTER TABLE platform.rag_chunks ALTER COLUMN embedding TYPE vector("
                + dimensions.Value.ToString(CultureInfo.InvariantCulture) + ")";
            var create = "CREATE INDEX IF NOT EXISTS \"" + IndexName
                + "\" ON platform.rag_chunks USING hnsw (embedding vector_cosine_ops)";

            // Both are no-ops if a previous run already got here.
            await db.Database.ExecuteSqlRawAsync(alter, cancellationToken);
            await db.Database.ExecuteSqlRawAsync(create, cancellationToken);

            logger.LogInformation("HNSW index is in place for rag_chunks.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mixed embedding dimensions, a permission the app role lacks, or a pgvector too old to
            // index. Retrieval keeps working on exact scan; say so rather than failing the ingest.
            logger.LogWarning(ex, "Could not promote rag_chunks to an HNSW index; retrieval continues with exact scan.");
        }
    }

    private async Task<bool> IndexExistsAsync(CancellationToken cancellationToken)
    {
        var found = await db.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'platform' AND indexname = {IndexName}")
            .ToListAsync(cancellationToken);
        return found.Count > 0;
    }

    private async Task<int?> DimensionsAsync(CancellationToken cancellationToken)
    {
        var dims = await db.Database
            .SqlQuery<int?>($"""
                SELECT vector_dims(embedding) AS "Value"
                FROM platform.rag_chunks
                WHERE embedding IS NOT NULL
                LIMIT 1
                """)
            .ToListAsync(cancellationToken);
        return dims.Count > 0 ? dims[0] : null;
    }
}

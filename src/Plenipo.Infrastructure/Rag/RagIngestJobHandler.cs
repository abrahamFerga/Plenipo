using System.Text.Json;
using Plenipo.Application.Jobs;
using Plenipo.Application.Rag;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// Ingests files into a RAG collection as a background job — extraction and embedding are too slow
/// for a chat turn. Runs under the enqueuer's captured authority (like every job), with per-file
/// progress; ingestion is idempotent per file, so a re-run refreshes rather than duplicates.
/// </summary>
public sealed class RagIngestJobHandler : IJobHandler
{
    public string Kind => RagIngestJob.Kind;

    public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Deserialize<RagIngestArgs>(context.ArgumentsJson, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("RAG ingest arguments are missing.");
        if (args.FileIds.Count == 0)
        {
            throw new InvalidOperationException("RAG ingest needs at least one file.");
        }

        var rag = context.ScopedServices.GetRequiredService<IRagService>();
        var options = new RagIngestOptions
        {
            Principals = args.Principals,
            Metadata = args.Metadata,
            Language = args.Language,
        };

        var chunks = 0;
        var unreadable = 0;
        for (var i = 0; i < args.FileIds.Count; i++)
        {
            await context.ReportProgressAsync(
                (int)(i * 100.0 / args.FileIds.Count),
                $"{i}/{args.FileIds.Count} documents indexed",
                cancellationToken);

            var stored = await rag.IngestFileAsync(args.CollectionId, args.FileIds[i], options, cancellationToken);
            chunks += stored;
            if (stored == 0)
            {
                unreadable++;
            }
        }

        await context.ReportProgressAsync(100, $"{args.FileIds.Count}/{args.FileIds.Count} documents indexed", cancellationToken);

        // Once the corpus outgrows exact scan, promote it — here rather than at query time, because
        // building an index belongs in the background job that just made it necessary.
        await context.ScopedServices.GetRequiredService<RagIndexMaintenance>()
            .EnsureVectorIndexAsync(cancellationToken);

        return JsonSerializer.Serialize(
            new { files = args.FileIds.Count, chunks, unreadable },
            JsonSerializerOptions.Web);
    }
}

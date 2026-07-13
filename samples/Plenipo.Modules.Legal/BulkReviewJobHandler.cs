using System.Text;
using System.Text.Json;
using Plenipo.Application.Documents;
using Plenipo.Application.Files;
using Plenipo.Application.Jobs;
using Plenipo.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Modules.Legal;

/// <summary>Arguments for a <c>legal.bulk-review</c> job.</summary>
public sealed record BulkReviewArgs(Guid MatterId, string MatterName, IReadOnlyList<string> Questions);

/// <summary>
/// The bulk review table (Harvey's Review Tables / Legora's Tabular Review, v1 scale): every
/// document on a matter × every question, executed as a background job with per-document progress.
/// Answers are keyword-grounded EXCERPTS — literal quotes from the document with a file citation —
/// so the result is verifiable and hallucination-free with no model in the loop. (Upgrading a cell
/// to a model-composed answer is a deliberate seam: see <see cref="AnswerAsync"/>.) The finished
/// table lands on the matter as a PDF, and the structured result stays on the job for the API/UI.
/// </summary>
public sealed class BulkReviewJobHandler : IJobHandler
{
    public const string JobKind = "legal.bulk-review";

    public string Kind => JobKind;

    public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Deserialize<BulkReviewArgs>(context.ArgumentsJson, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Bulk review arguments are missing.");
        if (args.Questions.Count == 0)
        {
            throw new InvalidOperationException("Bulk review needs at least one question.");
        }

        var services = context.ScopedServices;
        var db = services.GetRequiredService<LegalDbContext>();
        var reader = services.GetRequiredService<IDocumentReader>();
        var renderer = services.GetRequiredService<IPdfRenderer>();
        var files = services.GetRequiredService<IFileStore>();

        var matterDocs = await db.MatterDocuments
            .Where(d => d.MatterId == args.MatterId)
            .OrderBy(d => d.FileName)
            .ToListAsync(cancellationToken);
        if (matterDocs.Count == 0)
        {
            throw new InvalidOperationException($"Matter '{args.MatterName}' has no documents to review.");
        }

        var rows = new List<ReviewRow>();
        for (var i = 0; i < matterDocs.Count; i++)
        {
            var doc = matterDocs[i];
            await context.ReportProgressAsync(
                (int)(i * 100.0 / matterDocs.Count),
                $"{i}/{matterDocs.Count} documents reviewed",
                cancellationToken);

            var text = await reader.ExtractTextAsync(doc.FileId, cancellationToken)
                ?? $"[{doc.FileName} is not a readable document]";
            var cells = args.Questions
                .Select(q => new ReviewCell(q, Answer(text, q, doc.FileName, doc.FileId)))
                .ToList();
            rows.Add(new ReviewRow(doc.FileId, doc.FileName, cells));
        }

        // File the table on the matter as work product (same pattern as the review memo).
        var pdf = renderer.Render($"Bulk review — {args.MatterName}", RenderTable(args, rows));
        using var stream = new MemoryStream(pdf);
        var stored = await files.SaveAsync(
            $"bulk-review-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf", "application/pdf", stream,
            source: "bulk_review", cancellationToken);

        db.MatterDocuments.Add(new MatterDocument
        {
            TenantId = context.TenantId,
            MatterId = args.MatterId,
            FileId = stored.Id,
            FileName = stored.FileName,
            Note = $"bulk review table ({args.Questions.Count} question(s) × {rows.Count} document(s))",
        });
        await db.SaveChangesAsync(cancellationToken);

        await context.ReportProgressAsync(100, $"{matterDocs.Count}/{matterDocs.Count} documents reviewed", cancellationToken);
        return JsonSerializer.Serialize(new ReviewResult(stored.Id, args.Questions, rows), JsonSerializerOptions.Web);
    }

    /// <summary>
    /// Answers one cell from the document text: the sentences whose words match the question,
    /// quoted verbatim with a citation. THE MODEL SEAM: a deployment with a real provider can
    /// replace this with a per-cell LLM call over the same inputs — the contract (grounded answer +
    /// citation) stays identical.
    /// </summary>
    private static string Answer(string documentText, string question, string fileName, Guid fileId)
    {
        var sentences = documentText
            .Split(['.', '\n', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 10)
            .ToList();

        // Stemmed prefixes so morphology doesn't hide matches: "termination" must hit "terminate",
        // "rights" must hit "right". Crude English suffix-stripping is enough for keyword grounding.
        var stems = question
            .Split([' ', ',', '?', '!', '\'', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .Select(Stem)
            .Where(s => s.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var hits = sentences
            .Where(s => stems.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Take(3)
            .ToList();

        return hits.Count == 0
            ? $"No matching passage found in {fileName} (file id: {fileId})."
            : string.Join(" … ", hits.Select(h => $"\"{h}\"")) + $" (source: {fileName}, file id: {fileId})";
    }

    /// <summary>Strips common English suffixes so keyword prefixes match across word forms.</summary>
    private static string Stem(string word)
    {
        foreach (var suffix in (string[])["ation", "tion", "sion", "ment", "ing", "ies", "ed", "es", "s"])
        {
            if (word.Length - suffix.Length >= 4 && word.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return word[..^suffix.Length];
            }
        }

        return word;
    }

    private static string RenderTable(BulkReviewArgs args, List<ReviewRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Matter: {args.MatterName}. {rows.Count} document(s) × {args.Questions.Count} question(s). Answers are verbatim excerpts with citations; attorney review required.");
        foreach (var row in rows)
        {
            sb.AppendLine();
            sb.AppendLine($"Document: {row.FileName} (file id: {row.FileId})");
            foreach (var cell in row.Cells)
            {
                sb.AppendLine();
                sb.AppendLine($"Q: {cell.Question}");
                sb.AppendLine($"A: {cell.Answer}");
            }
        }

        return sb.ToString();
    }

    public sealed record ReviewCell(string Question, string Answer);

    public sealed record ReviewRow(Guid FileId, string FileName, IReadOnlyList<ReviewCell> Cells);

    public sealed record ReviewResult(Guid ReportFileId, IReadOnlyList<string> Questions, IReadOnlyList<ReviewRow> Rows);
}

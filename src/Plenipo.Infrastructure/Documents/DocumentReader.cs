using Plenipo.Application.Documents;
using Plenipo.Application.Files;

namespace Plenipo.Infrastructure.Documents;

/// <summary>The extraction core shared by the <c>read_document</c> tool and module code.</summary>
public sealed class DocumentReader(IFileStore files, IOcrEngine? ocr = null) : IDocumentReader
{
    public async Task<string?> ExtractTextAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        (await ExtractAsync(fileId, cancellationToken))?.Text;

    public async Task<DocumentText?> ExtractAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var file = await files.FindAsync(fileId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        await using var content = await files.OpenReadAsync(fileId, cancellationToken);
        if (content is null)
        {
            return null;
        }

        if (DocumentTools.IsPdf(file.ContentType, file.FileName))
        {
            var extracted = DocumentTools.ExtractPdfPages(content);
            if (!string.IsNullOrWhiteSpace(extracted.Text))
            {
                return extracted;
            }

            if (ocr is not null)
            {
                // A scan has no text layer; the OCR engine decides whether it can report pages.
                content.Position = 0;
                return await ocr.ExtractAsync(content, file.ContentType, cancellationToken);
            }

            return null;
        }

        if (file.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            file.ContentType is "application/json" or "application/xml")
        {
            using var reader = new StreamReader(content);
            var text = await reader.ReadToEndAsync(cancellationToken);
            // Normalised here so offsets computed by any consumer stay valid — plain text has no
            // pages, so the citation names the file alone.
            return DocumentText.Unpaged(text.ReplaceLineEndings("\n"));
        }

        return null;
    }
}

/// <summary>PDF rendering for module code — delegates to the shared PdfPig layout.</summary>
public sealed class PdfRenderer : IPdfRenderer
{
    public byte[] Render(string title, string body) => DocumentTools.BuildPdf(title, body);
}

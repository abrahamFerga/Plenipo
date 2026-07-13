using Plenipo.Application.Documents;
using Plenipo.Application.Files;

namespace Plenipo.Infrastructure.Documents;

/// <summary>The extraction core shared by the <c>read_document</c> tool and module code.</summary>
public sealed class DocumentReader(IFileStore files, IOcrEngine? ocr = null) : IDocumentReader
{
    public async Task<string?> ExtractTextAsync(Guid fileId, CancellationToken cancellationToken = default)
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
            var text = DocumentTools.ExtractPdfText(content);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            if (ocr is not null)
            {
                content.Position = 0;
                return await ocr.ExtractTextAsync(content, file.ContentType, cancellationToken);
            }

            return null;
        }

        if (file.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            file.ContentType is "application/json" or "application/xml")
        {
            using var reader = new StreamReader(content);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        return null;
    }
}

/// <summary>PDF rendering for module code — delegates to the shared PdfPig layout.</summary>
public sealed class PdfRenderer : IPdfRenderer
{
    public byte[] Render(string title, string body) => DocumentTools.BuildPdf(title, body);
}

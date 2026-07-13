namespace Plenipo.Application.Documents;

/// <summary>
/// Extracts the text of a stored file for module code (job handlers, report generators) — the same
/// extraction the agent's <c>read_document</c> tool uses (PDF text layer, plain text, OCR fallback
/// when an engine is configured), without the agent-facing message wrapping. Tenant-scoped via the
/// file store: a foreign tenant's id behaves like a missing one.
/// </summary>
public interface IDocumentReader
{
    /// <summary>The file's text, or null when the file doesn't exist or isn't a readable document.</summary>
    public Task<string?> ExtractTextAsync(Guid fileId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Renders simple text work product (title + paragraphs) to a PDF — the same dependency-free layout
/// the agent's <c>generate_pdf</c> tool uses, for module code that files reports directly.
/// </summary>
public interface IPdfRenderer
{
    public byte[] Render(string title, string body);
}

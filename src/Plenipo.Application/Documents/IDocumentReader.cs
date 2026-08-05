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

    /// <summary>
    /// The same extraction, plus page boundaries when the source is paginated and the extractor can
    /// report them. Retrieval uses this so a cited passage can name its page; callers that only need
    /// the text should keep using <see cref="ExtractTextAsync"/>. The default wraps
    /// <see cref="ExtractTextAsync"/> with no page information, so implementations written before
    /// this member existed keep compiling and behave as an unpaginated source.
    /// </summary>
    public async Task<DocumentText?> ExtractAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var text = await ExtractTextAsync(fileId, cancellationToken);
        return text is null ? null : DocumentText.Unpaged(text);
    }
}

/// <summary>
/// Renders simple text work product (title + paragraphs) to a PDF — the same dependency-free layout
/// the agent's <c>generate_pdf</c> tool uses, for module code that files reports directly.
/// </summary>
public interface IPdfRenderer
{
    public byte[] Render(string title, string body);
}

namespace Plenipo.Application.Documents;

/// <summary>
/// Optical character recognition over scanned documents/images. The platform ships no OCR engine by
/// default (native OCR dependencies are heavy and deployment-specific) — this seam lets a host plug
/// one in (e.g. a Tesseract wrapper or an Azure AI Document Intelligence client) and the agent's
/// <c>ocr_document</c> tool appears automatically wherever an engine is registered.
/// </summary>
public interface IOcrEngine
{
    /// <summary>A short human-readable engine name for diagnostics (e.g. "tesseract").</summary>
    public string Name { get; }

    /// <summary>Extracts text from a scanned PDF or image. Null when the content can't be processed.</summary>
    public Task<string?> ExtractTextAsync(Stream content, string contentType, CancellationToken cancellationToken = default);
}

using Azure;
using Azure.AI.DocumentIntelligence;
using Plenipo.Application.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Both namespaces define DocumentPage; ours is the one this file constructs.
using PlatformPage = Plenipo.Application.Documents.DocumentPage;

namespace Plenipo.Infrastructure.Documents;

/// <summary>
/// The Azure AI Document Intelligence OCR engine (config-driven: <c>Ocr:Provider=AzureDocumentIntelligence</c>
/// + endpoint + key). Uses the prebuilt "read" model — plain text extraction from scanned PDFs and
/// images, no custom training. Registered only when configured, so deployments without the Azure
/// resource simply don't have OCR (and the <c>ocr_document</c> tool never appears).
/// </summary>
public sealed class AzureDocumentIntelligenceOcrEngine : IOcrEngine
{
    private readonly DocumentIntelligenceClient _client;
    private readonly ILogger<AzureDocumentIntelligenceOcrEngine> _logger;

    public AzureDocumentIntelligenceOcrEngine(
        IOptions<OcrOptions> options, ILogger<AzureDocumentIntelligenceOcrEngine> logger)
    {
        var value = options.Value;
        _client = new DocumentIntelligenceClient(new Uri(value.Endpoint!), new AzureKeyCredential(value.ApiKey!));
        _logger = logger;
    }

    public string Name => "azure-document-intelligence";

    public async Task<string?> ExtractTextAsync(
        Stream content, string contentType, CancellationToken cancellationToken = default) =>
        (await ExtractAsync(content, contentType, cancellationToken))?.Text;

    /// <summary>
    /// Document Intelligence reports each page as a span into the analysed content, so a scanned
    /// document gets the same page citations a born-digital PDF does — which is the common case in
    /// document-heavy domains, where most of the corpus arrives as scans.
    /// </summary>
    public async Task<DocumentText?> ExtractAsync(
        Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        try
        {
            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                new AnalyzeDocumentOptions("prebuilt-read", await BinaryData.FromStreamAsync(content, cancellationToken)),
                cancellationToken);

            var text = operation.Value.Content;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var pages = new List<PlatformPage>();
            foreach (var page in operation.Value.Pages)
            {
                // A page can carry several spans; its extent is the first start to the last end.
                // Clamped to the content length because the spans and the string must agree.
                var spans = page.Spans;
                if (spans is not { Count: > 0 })
                {
                    continue;
                }

                var start = Math.Clamp(spans.Min(s => s.Offset), 0, text.Length);
                var end = Math.Clamp(spans.Max(s => s.Offset + s.Length), start, text.Length);
                pages.Add(new PlatformPage(page.PageNumber, start, end));
            }

            return new DocumentText(text, pages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RequestFailedException ex)
        {
            // An unprocessable document (wrong format, corrupt scan) is a null result by the seam's
            // contract, not an exception the caller has to unpack per provider.
            _logger.LogWarning(ex, "Document Intelligence could not process a {ContentType} document", contentType);
            return null;
        }
    }
}

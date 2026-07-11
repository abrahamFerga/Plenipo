using Azure;
using Azure.AI.DocumentIntelligence;
using Cortex.Application.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Infrastructure.Documents;

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
        Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        try
        {
            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                new AnalyzeDocumentOptions("prebuilt-read", await BinaryData.FromStreamAsync(content, cancellationToken)),
                cancellationToken);
            var text = operation.Value.Content;
            return string.IsNullOrWhiteSpace(text) ? null : text;
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

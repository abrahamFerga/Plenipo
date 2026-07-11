namespace Cortex.Application.Documents;

/// <summary>
/// OCR engine selection (the "Ocr" section). The platform ships no engine by default; setting
/// <c>Provider=AzureDocumentIntelligence</c> with an endpoint + key turns scanned-PDF/image text
/// extraction on everywhere the <see cref="IOcrEngine"/> seam is consumed (the agent's
/// <c>ocr_document</c> tool, document reading, product statement extractors).
/// </summary>
public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    public const string AzureDocumentIntelligenceProvider = "AzureDocumentIntelligence";

    /// <summary>None (default) or AzureDocumentIntelligence.</summary>
    public string Provider { get; set; } = "None";

    /// <summary>The Document Intelligence resource endpoint, e.g. https://myresource.cognitiveservices.azure.com/.</summary>
    public string? Endpoint { get; set; }

    /// <summary>SECRET — via user-secrets / env (Ocr__ApiKey) / Key Vault, never appsettings.</summary>
    public string? ApiKey { get; set; }

    public bool IsAzureDocumentIntelligence =>
        string.Equals(Provider, AzureDocumentIntelligenceProvider, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(ApiKey);
}

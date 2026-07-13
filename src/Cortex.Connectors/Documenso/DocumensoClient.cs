using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Application.Security;

namespace Cortex.Connectors.Documenso;

/// <summary>The tenant's Documenso connection, resolved from protected connector settings per call.</summary>
public sealed record DocumensoConnection(string BaseUrl, string ApiToken);

/// <summary>What the connector sends out for signature.</summary>
public sealed record SignatureRequest(
    string Title, string FileName, byte[] Content, string RecipientEmail, string RecipientName);

/// <summary>A signature request as the status tool reports it.</summary>
public sealed record SignatureStatus(string DocumentId, string Status, string Recipients);

/// <summary>A completed document downloaded back from Documenso.</summary>
public sealed record SignedDocument(Stream Content, string FileName);

/// <summary>
/// The slice of the Documenso REST API the connector needs — a seam so keyless tests fake the
/// signing service while production speaks HTTP to app.documenso.com or a self-hosted instance
/// (Documenso is the open-source e-signature platform; we integrate over its public API only).
/// </summary>
public interface IDocumensoClient
{
    /// <summary>Creates a document, uploads the PDF, and dispatches it for signing. Returns the document id.</summary>
    public Task<string> SendForSignatureAsync(
        DocumensoConnection connection, SignatureRequest request, CancellationToken cancellationToken = default);

    public Task<SignatureStatus?> GetStatusAsync(
        DocumensoConnection connection, string documentId, CancellationToken cancellationToken = default);

    /// <summary>Downloads the completed (signed) document; null when it doesn't exist or isn't finished.</summary>
    public Task<SignedDocument?> DownloadSignedAsync(
        DocumensoConnection connection, string documentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Documenso public API v1 over plain HTTP (no vendor SDK): create → upload to the presigned
/// URL → send; status by GET; completed download via the returned URL. The API token goes in
/// the Authorization header as Documenso issues it.
/// </summary>
public sealed class DocumensoApiClient(
    IHttpClientFactory httpClientFactory,
    OutboundUrlPolicy outboundUrls) : IDocumensoClient
{
    public const string HttpClientName = "documenso";

    public async Task<string> SendForSignatureAsync(
        DocumensoConnection connection, SignatureRequest request, CancellationToken cancellationToken = default)
    {
        var http = await CreateClientAsync(connection, cancellationToken);

        var created = await http.PostAsJsonAsync("api/v1/documents", new
        {
            title = request.Title,
            recipients = new[] { new { name = request.RecipientName, email = request.RecipientEmail, role = "SIGNER" } },
        }, cancellationToken);
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync(cancellationToken));
        var documentId = createdJson.RootElement.GetProperty("documentId").ToString();
        var uploadUrl = createdJson.RootElement.GetProperty("uploadUrl").GetString()!;

        // The upload URL is presigned — no auth header, raw PDF bytes.
        using var uploadContent = new ByteArrayContent(request.Content);
        uploadContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        using var plain = httpClientFactory.CreateClient(HttpClientName);
        var uploadDestination = await outboundUrls.RequireAllowedAsync(uploadUrl, cancellationToken);
        (await plain.PutAsync(uploadDestination, uploadContent, cancellationToken)).EnsureSuccessStatusCode();

        (await http.PostAsync($"api/v1/documents/{documentId}/send", content: null, cancellationToken))
            .EnsureSuccessStatusCode();

        return documentId;
    }

    public async Task<SignatureStatus?> GetStatusAsync(
        DocumensoConnection connection, string documentId, CancellationToken cancellationToken = default)
    {
        var http = await CreateClientAsync(connection, cancellationToken);
        var response = await http.GetAsync($"api/v1/documents/{documentId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = json.RootElement;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "UNKNOWN" : "UNKNOWN";
        var recipients = root.TryGetProperty("recipients", out var r) && r.ValueKind == JsonValueKind.Array
            ? string.Join("; ", r.EnumerateArray().Select(x =>
                $"{x.GetProperty("email").GetString()}: {(x.TryGetProperty("signingStatus", out var ss) ? ss.GetString() : "?")}"))
            : "";
        return new SignatureStatus(documentId, status, recipients);
    }

    public async Task<SignedDocument?> DownloadSignedAsync(
        DocumensoConnection connection, string documentId, CancellationToken cancellationToken = default)
    {
        var http = await CreateClientAsync(connection, cancellationToken);
        var response = await http.GetAsync($"api/v1/documents/{documentId}/download", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("downloadUrl", out var url) || url.GetString() is not { } downloadUrl)
        {
            return null;
        }

        using var plain = httpClientFactory.CreateClient(HttpClientName);
        var downloadDestination = await outboundUrls.RequireAllowedAsync(downloadUrl, cancellationToken);
        var file = await plain.GetAsync(downloadDestination, cancellationToken);
        if (!file.IsSuccessStatusCode)
        {
            return null;
        }

        var buffer = new MemoryStream(await file.Content.ReadAsByteArrayAsync(cancellationToken));
        return new SignedDocument(buffer, $"signed-{documentId}.pdf");
    }

    private async Task<HttpClient> CreateClientAsync(
        DocumensoConnection connection, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        http.BaseAddress = await outboundUrls.RequireAllowedAsync(
            connection.BaseUrl.TrimEnd('/') + "/", cancellationToken);
        // Documenso v1 API tokens are sent as the raw Authorization header value.
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", connection.ApiToken);
        return http;
    }
}

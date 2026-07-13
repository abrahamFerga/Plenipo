using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Connectors.MsGraph;

/// <summary>One drive item as the tools see it.</summary>
public sealed record GraphDriveItem(string Id, string Name, long Size, bool IsFolder);

/// <summary>A downloaded drive item's content.</summary>
public sealed record GraphFileContent(Stream Content, string ContentType);

/// <summary>
/// The slice of Microsoft Graph the connector needs (list/download drive items) — a seam so
/// keyless tests fake Graph while production speaks plain Graph v1.0 REST. Deliberately not the
/// Microsoft.Graph SDK: two endpoints don't justify its dependency surface.
/// </summary>
public interface IGraphApiClient
{
    public Task<IReadOnlyList<GraphDriveItem>> ListDriveItemsAsync(
        string accessToken, string? folderPath, CancellationToken cancellationToken = default);

    public Task<GraphFileContent?> DownloadAsync(
        string accessToken, string itemId, CancellationToken cancellationToken = default);
}

/// <summary>Graph v1.0 over HttpClient, on the caller's delegated token (/me/drive).</summary>
public sealed class GraphApiClient(IHttpClientFactory httpClients) : IGraphApiClient
{
    public const string HttpClientName = "msgraph";

    private const string BaseUrl = "https://graph.microsoft.com/v1.0";

    public async Task<IReadOnlyList<GraphDriveItem>> ListDriveItemsAsync(
        string accessToken, string? folderPath, CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(folderPath)
            ? $"{BaseUrl}/me/drive/root/children"
            : $"{BaseUrl}/me/drive/root:/{Uri.EscapeDataString(folderPath.Trim('/'))}:/children";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var client = httpClients.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return json.GetProperty("value").EnumerateArray()
            .Select(item => new GraphDriveItem(
                item.GetProperty("id").GetString()!,
                item.GetProperty("name").GetString()!,
                item.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                item.TryGetProperty("folder", out _)))
            .ToList();
    }

    public async Task<GraphFileContent?> DownloadAsync(
        string accessToken, string itemId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{BaseUrl}/me/drive/items/{Uri.EscapeDataString(itemId)}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var client = httpClients.CreateClient(HttpClientName);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            return null;
        }

        return new GraphFileContent(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }
}

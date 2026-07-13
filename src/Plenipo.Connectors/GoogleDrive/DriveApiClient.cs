using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Connectors.GoogleDrive;

/// <summary>One Drive file as the tools see it.</summary>
public sealed record DriveFile(string Id, string Name, long Size, bool IsFolder);

/// <summary>A downloaded Drive file's content.</summary>
public sealed record DriveFileContent(Stream Content, string ContentType);

/// <summary>
/// The slice of the Google Drive API the connector needs (list/download files) — a seam so
/// keyless tests fake Drive while production speaks plain Drive v3 REST. Deliberately not the
/// Google SDK: two endpoints don't justify its dependency surface.
/// </summary>
public interface IDriveApiClient
{
    public Task<IReadOnlyList<DriveFile>> ListFilesAsync(
        string accessToken, string? nameContains, CancellationToken cancellationToken = default);

    public Task<DriveFileContent?> DownloadAsync(
        string accessToken, string fileId, CancellationToken cancellationToken = default);
}

/// <summary>Drive v3 over HttpClient, on the caller's delegated token.</summary>
public sealed class DriveApiClient(IHttpClientFactory httpClients) : IDriveApiClient
{
    public const string HttpClientName = "google-drive";

    private const string BaseUrl = "https://www.googleapis.com/drive/v3";

    public async Task<IReadOnlyList<DriveFile>> ListFilesAsync(
        string accessToken, string? nameContains, CancellationToken cancellationToken = default)
    {
        var query = "trashed=false";
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            // Drive query strings quote values with single quotes; escape any in the fragment.
            var escaped = nameContains.Replace(@"\", @"\\", StringComparison.Ordinal)
                .Replace("'", @"\'", StringComparison.Ordinal);
            query += $" and name contains '{escaped}'";
        }

        var url = $"{BaseUrl}/files?pageSize=100&fields=files(id,name,size,mimeType)&q={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var client = httpClients.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var files = new List<DriveFile>();
        if (payload.TryGetProperty("files", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in array.EnumerateArray())
            {
                var mime = f.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
                files.Add(new DriveFile(
                    f.GetProperty("id").GetString() ?? "",
                    f.GetProperty("name").GetString() ?? "",
                    f.TryGetProperty("size", out var size) && long.TryParse(size.GetString(), out var bytes) ? bytes : 0,
                    IsFolder: mime == "application/vnd.google-apps.folder"));
            }
        }

        return files;
    }

    public async Task<DriveFileContent?> DownloadAsync(
        string accessToken, string fileId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/files/{Uri.EscapeDataString(fileId)}?alt=media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var client = httpClients.CreateClient(HttpClientName);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            return null;
        }

        return new DriveFileContent(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }
}

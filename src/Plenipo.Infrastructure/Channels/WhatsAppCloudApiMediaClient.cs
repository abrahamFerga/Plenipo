using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Plenipo.Application.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Channels;

/// <summary>
/// <see cref="IWhatsAppMediaClient"/> over the Meta Cloud API's two-step media download:
/// <c>GET {ApiBaseUrl}/{mediaId}</c> returns a short-lived URL + mime type, then a bearer-authorized
/// GET on that URL returns the bytes.
/// </summary>
public sealed class WhatsAppCloudApiMediaClient(
    HttpClient http,
    IOptions<WhatsAppOptions> options,
    ILogger<WhatsAppCloudApiMediaClient> logger) : IWhatsAppMediaClient
{
    public async Task<WhatsAppMedia?> DownloadAsync(string mediaId, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var baseUrl = o.ApiBaseUrl.TrimEnd('/');

        using var lookup = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{Uri.EscapeDataString(mediaId)}");
        lookup.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);

        using var lookupResponse = await http.SendAsync(lookup, cancellationToken);
        if (!lookupResponse.IsSuccessStatusCode)
        {
            logger.LogError("WhatsApp media lookup for {MediaId} failed with {Status}.", mediaId, (int)lookupResponse.StatusCode);
            return null;
        }

        var descriptor = await lookupResponse.Content.ReadFromJsonAsync<MediaDescriptor>(cancellationToken);
        if (descriptor?.Url is null)
        {
            logger.LogError("WhatsApp media lookup for {MediaId} returned no URL.", mediaId);
            return null;
        }

        using var download = new HttpRequestMessage(HttpMethod.Get, descriptor.Url);
        download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);

        var downloadResponse = await http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!downloadResponse.IsSuccessStatusCode)
        {
            downloadResponse.Dispose();
            logger.LogError("WhatsApp media download for {MediaId} failed with {Status}.", mediaId, (int)downloadResponse.StatusCode);
            return null;
        }

        var contentType = downloadResponse.Content.Headers.ContentType?.MediaType
            ?? descriptor.MimeType
            ?? "application/octet-stream";
        return new WhatsAppMedia(await downloadResponse.Content.ReadAsStreamAsync(cancellationToken), contentType);
    }

    private sealed record MediaDescriptor
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("mime_type")]
        public string? MimeType { get; init; }
    }
}

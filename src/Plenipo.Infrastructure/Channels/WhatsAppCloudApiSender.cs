using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Plenipo.Application.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Channels;

/// <summary>
/// <see cref="IWhatsAppSender"/> over the Meta WhatsApp Business Cloud API
/// (<c>POST {ApiBaseUrl}/{PhoneNumberId}/messages</c>). Replies longer than WhatsApp's per-message
/// limit are split into consecutive messages rather than truncated.
/// </summary>
public sealed class WhatsAppCloudApiSender(
    HttpClient http,
    IOptions<WhatsAppOptions> options,
    ILogger<WhatsAppCloudApiSender> logger) : IWhatsAppSender
{
    public async Task SendTextAsync(string to, string text, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var baseUrl = o.ApiBaseUrl.TrimEnd('/');

        foreach (var chunk in Chunk(text, Math.Max(1, o.MaxMessageLength)))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{o.PhoneNumberId}/messages")
            {
                Content = JsonContent.Create(new OutboundText
                {
                    To = to,
                    Text = new OutboundText.Body { Text = chunk },
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Log-and-continue would silently drop the user's answer; surface it so the webhook
                // handler records the failure (Meta will redeliver the inbound message).
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError("WhatsApp send to {To} failed with {Status}: {Detail}", to, (int)response.StatusCode, detail);
                throw new HttpRequestException($"WhatsApp Cloud API returned {(int)response.StatusCode}.");
            }
        }
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }

    private sealed record OutboundText
    {
        [JsonPropertyName("messaging_product")]
        public string MessagingProduct { get; init; } = "whatsapp";

        [JsonPropertyName("to")]
        public required string To { get; init; }

        [JsonPropertyName("type")]
        public string Type { get; init; } = "text";

        [JsonPropertyName("text")]
        public required Body Text { get; init; }

        public sealed record Body
        {
            [JsonPropertyName("body")]
            public required string Text { get; init; }
        }
    }
}

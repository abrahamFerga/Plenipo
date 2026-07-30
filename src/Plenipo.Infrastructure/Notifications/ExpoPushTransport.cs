using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Plenipo.Application.Notifications;
using Plenipo.Application.Security;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Notifications;

/// <summary>
/// The built-in <see cref="IPushTransport"/>: Expo's push service, which fronts both APNs and FCM
/// with one HTTP call and one token format. That makes the whole mobile push path work with no
/// Apple or Google credentials in the repo and none in CI — the platform's keyless-by-default rule
/// applied to notifications.
/// <para>
/// A deployment that outgrows it (its own FCM/APNs keys, an MDM gateway, a corporate relay) swaps
/// this out with a single DI registration; nothing above this class knows Expo exists.
/// </para>
/// </summary>
public sealed class ExpoPushTransport(
    IHttpClientFactory httpClientFactory,
    IOptions<PushOptions> options,
    OutboundUrlPolicy outboundUrls) : IPushTransport
{
    public const string HttpClientName = "plenipo-push-expo";

    /// <summary>Expo's own cap on tickets per request.</summary>
    private const int BatchSize = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PushResult>> SendAsync(
        IReadOnlyList<PushMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            return [];
        }

        var settings = options.Value;
        var endpoint = await outboundUrls.RequireAllowedAsync(settings.ExpoEndpoint, cancellationToken);
        var client = httpClientFactory.CreateClient(HttpClientName);
        var results = new List<PushResult>(messages.Count);

        for (var offset = 0; offset < messages.Count; offset += BatchSize)
        {
            var batch = messages.Skip(offset).Take(BatchSize).ToList();
            results.AddRange(await SendBatchAsync(client, endpoint, settings, batch, cancellationToken));
        }

        return results;
    }

    private static async Task<IReadOnlyList<PushResult>> SendBatchAsync(
        HttpClient client,
        Uri endpoint,
        PushOptions settings,
        List<PushMessage> batch,
        CancellationToken cancellationToken)
    {
        var payload = batch.Select(m => new ExpoMessage
        {
            To = m.Token,
            Title = m.Title,
            Body = m.Body,
            // The tap target and the producing category ride as data so the shell can deep-link
            // straight to the record instead of dumping the user on the home tab.
            Data = new Dictionary<string, string?> { ["link"] = m.Link, ["category"] = m.Category },
            // Collapse repeats of the same category rather than stacking twelve identical buzzes.
            CollapseId = m.Category,
        }).ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        if (!string.IsNullOrWhiteSpace(settings.ExpoAccessToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.ExpoAccessToken}");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            // The whole batch is unreachable — transient by assumption, so every device is kept.
            return [.. batch.Select(m => new PushResult(m.Token, PushDeliveryStatus.Failed, ex.Message))];
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var reason = $"{(int)response.StatusCode} {response.ReasonPhrase}";
                return [.. batch.Select(m => new PushResult(m.Token, PushDeliveryStatus.Failed, reason))];
            }

            var envelope = await response.Content.ReadFromJsonAsync<ExpoResponse>(Json, cancellationToken);
            var tickets = envelope?.Data;
            if (tickets is null || tickets.Length != batch.Count)
            {
                // A response we can't line up with the batch tells us nothing per device. Treat it
                // as transient rather than guessing which token to delete.
                return [.. batch.Select(m => new PushResult(m.Token, PushDeliveryStatus.Failed, "unexpected push response shape"))];
            }

            return [.. batch.Select((m, i) => Interpret(m.Token, tickets[i]))];
        }
    }

    /// <summary>
    /// One Expo ticket → one outcome. Only <c>DeviceNotRegistered</c> is permanent; everything else
    /// (rate limits, provider hiccups, a message too big) leaves the device registered so the next
    /// notification tries again.
    /// </summary>
    private static PushResult Interpret(string token, ExpoTicket ticket)
    {
        if (string.Equals(ticket.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return new PushResult(token, PushDeliveryStatus.Delivered);
        }

        var code = ticket.Details?.Error;
        var status = string.Equals(code, "DeviceNotRegistered", StringComparison.Ordinal)
            ? PushDeliveryStatus.TokenGone
            : PushDeliveryStatus.Failed;
        return new PushResult(token, status, ticket.Message ?? code);
    }

    private sealed class ExpoMessage
    {
        [JsonPropertyName("to")]
        public required string To { get; init; }

        [JsonPropertyName("title")]
        public required string Title { get; init; }

        [JsonPropertyName("body")]
        public required string Body { get; init; }

        [JsonPropertyName("data")]
        public Dictionary<string, string?>? Data { get; init; }

        [JsonPropertyName("collapseId")]
        public string? CollapseId { get; init; }
    }

    private sealed class ExpoResponse
    {
        [JsonPropertyName("data")]
        public ExpoTicket[]? Data { get; init; }
    }

    private sealed class ExpoTicket
    {
        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }

        [JsonPropertyName("details")]
        public ExpoTicketDetails? Details { get; init; }
    }

    private sealed class ExpoTicketDetails
    {
        [JsonPropertyName("error")]
        public string? Error { get; init; }
    }
}

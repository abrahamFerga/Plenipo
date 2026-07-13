using System.Collections.Concurrent;
using System.Text.Json;
using Plenipo.Application.Channels;
using Plenipo.Infrastructure.Channels;
using Microsoft.Extensions.Options;

namespace Plenipo.AspNetCore.Channels;

/// <summary>
/// Wires the WhatsApp channel: options (validated at startup, fail-fast), the Cloud API sender, the
/// inbound processor, and the two webhook endpoints Meta calls. The webhook is necessarily anonymous —
/// authentication is the HMAC app-secret signature on every delivery, verified before any processing.
/// When <c>Channels:WhatsApp:Enabled</c> is false (the default) the endpoints return 404 and nothing
/// else is active, so hosts that don't use WhatsApp carry no behavior change.
/// </summary>
public static class WhatsAppChannelSetup
{
    public static IServiceCollection AddPlenipoWhatsAppChannel(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.SectionName));

        // Fail fast on an enabled-but-incomplete channel, at startup rather than on Meta's first delivery.
        var options = configuration.GetSection(WhatsAppOptions.SectionName).Get<WhatsAppOptions>() ?? new WhatsAppOptions();
        options.ThrowIfInvalid();

        services.AddSingleton<WhatsAppMessageDeduplicator>();
        services.AddScoped<WhatsAppChannelService>();
        services.AddHttpClient<IWhatsAppSender, WhatsAppCloudApiSender>();
        services.AddHttpClient<IWhatsAppMediaClient, WhatsAppCloudApiMediaClient>();

        return services;
    }

    public static void MapWhatsAppChannel(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channels/whatsapp").WithTags("Channels");

        // Meta's one-time webhook verification handshake (hub.challenge echo).
        group.MapGet("/webhook", (
                HttpRequest request,
                IOptions<WhatsAppOptions> options) =>
            {
                var o = options.Value;
                if (!o.Enabled)
                {
                    return Results.NotFound();
                }

                var query = request.Query;
                return query["hub.mode"] == "subscribe" && query["hub.verify_token"] == o.VerifyToken
                    ? Results.Text(query["hub.challenge"].ToString())
                    : Results.StatusCode(StatusCodes.Status403Forbidden);
            })
            .AllowAnonymous()
            .WithName("WhatsApp_Verify");

        group.MapPost("/webhook", async (
                HttpRequest request,
                IOptions<WhatsAppOptions> options,
                WhatsAppChannelService channel,
                WhatsAppMessageDeduplicator deduplicator,
                CancellationToken cancellationToken) =>
            {
                var o = options.Value;
                if (!o.Enabled)
                {
                    return Results.NotFound();
                }

                using var buffer = new MemoryStream();
                await request.Body.CopyToAsync(buffer, cancellationToken);
                var body = buffer.ToArray();

                if (!WhatsAppSignature.IsValid(body, request.Headers["X-Hub-Signature-256"], o.AppSecret!))
                {
                    return Results.Unauthorized();
                }

                WhatsAppWebhookPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<WhatsAppWebhookPayload>(body);
                }
                catch (JsonException)
                {
                    return Results.BadRequest();
                }

                if (payload is not null)
                {
                    // Meta redelivers on timeout/non-200, so drop message ids we've already handled.
                    payload = deduplicator.WithoutAlreadyHandled(payload);
                    await channel.ProcessAsync(payload, cancellationToken);
                }

                return Results.Ok();
            })
            .AllowAnonymous()
            .WithName("WhatsApp_Webhook");
    }
}

/// <summary>
/// Remembers recently handled WhatsApp message ids so Meta's redeliveries (it retries on any non-200 or
/// slow response) don't produce duplicate agent turns. In-memory and per-instance — a redelivery landing
/// on another replica may still be processed, which is acceptable-at-most-once-per-instance for v1.
/// </summary>
public sealed class WhatsAppMessageDeduplicator
{
    private const int Capacity = 2048;
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public WhatsAppWebhookPayload WithoutAlreadyHandled(WhatsAppWebhookPayload payload)
    {
        return payload with
        {
            Entries = payload.Entries?.Select(e => e with
            {
                Changes = e.Changes?.Select(c => c with
                {
                    Value = c.Value is null ? null : c.Value with
                    {
                        Messages = c.Value.Messages?.Where(m => m.Id is null || TryBegin(m.Id)).ToList(),
                    },
                }).ToList(),
            }).ToList(),
        };
    }

    private bool TryBegin(string messageId)
    {
        if (!_seen.TryAdd(messageId, 0))
        {
            return false;
        }

        _order.Enqueue(messageId);
        while (_order.Count > Capacity && _order.TryDequeue(out var oldest))
        {
            _seen.TryRemove(oldest, out _);
        }

        return true;
    }
}

using System.Net.Http;
using System.Text;
using System.Text.Json;
using Plenipo.Application.Notifications;
using Plenipo.Application.Secrets;
using Plenipo.Application.Security;

namespace Plenipo.Infrastructure.Notifications;

/// <summary>
/// Posts each notification to the tenant's configured webhook, HMAC-signed when a secret is set.
/// Fired from the notifier's fan-out, which already isolates failures — this channel can throw
/// freely (unreachable endpoint, non-2xx) without losing the in-app copy.
/// </summary>
public sealed class WebhookNotificationChannel(
    INotificationWebhookConfigReader configs,
    ISecretVault vault,
    IHttpClientFactory httpClientFactory,
    OutboundUrlPolicy outboundUrls) : INotificationChannel
{
    public const string HttpClientName = "plenipo-notification-webhook";

    /// <summary>Vault scope for webhook signing secrets.</summary>
    public const string SecretScope = "Plenipo.Notifications.WebhookSecret";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var config = await configs.GetAsync(notification.TenantId, cancellationToken);
        if (config is null || string.IsNullOrWhiteSpace(config.Url))
        {
            return; // webhook delivery not configured for this tenant
        }

        var payload = JsonSerializer.Serialize(new
        {
            category = notification.Category,
            title = notification.Title,
            body = notification.Body,
            link = notification.Link,
            userId = notification.UserId,
            sentAt = DateTimeOffset.UtcNow,
        }, Json);

        var destination = await outboundUrls.RequireAllowedAsync(config.Url, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, destination)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(config.SecretRef))
        {
            var secret = await vault.RevealAsync(SecretScope, config.SecretRef, cancellationToken);
            request.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, WebhookSignature.Compute(payload, secret));
        }

        using var response = await httpClientFactory.CreateClient(HttpClientName)
            .SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

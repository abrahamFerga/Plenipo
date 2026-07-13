using System.Security.Cryptography;
using System.Text;

namespace Plenipo.Application.Notifications;

/// <summary>The tenant's webhook delivery config: a URL and (optionally) a vault reference to the signing secret.</summary>
public sealed record NotificationWebhookConfig(string Url, string? SecretRef);

/// <summary>Reads a tenant's webhook config by explicit tenant id (producers run outside request scopes).</summary>
public interface INotificationWebhookConfigReader
{
    public Task<NotificationWebhookConfig?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Payload signing for outbound webhooks: <c>sha256=&lt;hex HMAC-SHA256(body)&gt;</c> in the
/// <c>X-Plenipo-Signature</c> header, so receivers can authenticate that the call came from this
/// deployment — the same scheme GitHub/Meta webhooks use. Pure for testability.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Plenipo-Signature";

    public static string Compute(string payload, string secret) =>
        "sha256=" + Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));
}

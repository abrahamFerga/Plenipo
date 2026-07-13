namespace Plenipo.Application.Notifications;

/// <summary>
/// A platform notification: something that happened that a user should see without having to be
/// in the chat when it happened (a background job finished, a reminder came due, a sync failed).
/// Tenant and user are explicit — producers (like the job processor) often run outside a request
/// scope where no ambient tenant exists.
/// </summary>
public sealed record Notification(
    Guid TenantId,
    Guid UserId,
    string Category,
    string Title,
    string Body,
    string? Link = null);

/// <summary>
/// An additional delivery transport (webhook, email, chat push …). The in-app inbox is NOT a
/// channel — it is the baseline the notifier always persists; channels are best-effort extras
/// whose failures never lose the notification.
/// </summary>
public interface INotificationChannel
{
    public Task SendAsync(Notification notification, CancellationToken cancellationToken = default);
}

/// <summary>Persists the in-app notification, then fans out to every registered channel.</summary>
public interface INotifier
{
    public Task NotifyAsync(Notification notification, CancellationToken cancellationToken = default);
}

/// <summary>First-class host extension points for notifications.</summary>
public static class NotificationRegistration
{
    /// <summary>
    /// Adds a delivery channel (email, SMS, chat-ops, …) alongside the built-in in-app and
    /// webhook channels. The notifier fans every notification out to ALL registered channels;
    /// a channel that fails is logged and never blocks the others.
    /// </summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddPlenipoNotificationChannel<TChannel>(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        where TChannel : class, INotificationChannel
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddScoped<INotificationChannel, TChannel>(services);
        return services;
    }
}

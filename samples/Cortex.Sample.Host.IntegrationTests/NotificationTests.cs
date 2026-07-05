using System.Net.Http.Json;
using Cortex.Application.Notifications;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// The notification seam end to end: a produced notification lands in the recipient's in-app
/// inbox (and only theirs), and the read lifecycle works. Producers (the job processor) call the
/// same INotifier these tests exercise directly.
/// </summary>
[Collection("api")]
public sealed class NotificationTests(IntegrationFixture fixture)
{
    private sealed record NotificationDto(
        Guid Id, string Category, string Title, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

    [Fact]
    public async Task Notify_LandsInTheRecipientsInbox_AndOnlyTheirs()
    {
        // Materialize two users by having them touch the API, then look up their ids.
        using var recipientClient = fixture.ClientFor("notify_recipient");
        using var bystanderClient = fixture.ClientFor("notify_bystander");
        (await recipientClient.GetAsync("/api/notifications/")).EnsureSuccessStatusCode();
        (await bystanderClient.GetAsync("/api/notifications/")).EnsureSuccessStatusCode();

        Guid tenantId, recipientId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var recipient = await db.Users.IgnoreQueryFilters()
                .FirstAsync(u => u.Subject == "it-notify_recipient");
            tenantId = recipient.TenantId;
            recipientId = recipient.Id;

            var notifier = scope.ServiceProvider.GetRequiredService<INotifier>();
            await notifier.NotifyAsync(new Notification(
                tenantId, recipientId, "jobs", "Job finished: test.kind", "All done.", "/api/jobs/123"));
        }

        var inbox = await recipientClient.GetFromJsonAsync<List<NotificationDto>>("/api/notifications/?unreadOnly=true");
        var item = Assert.Single(inbox!, n => n.Title == "Job finished: test.kind");
        Assert.Equal("jobs", item.Category);
        Assert.Null(item.ReadAt);

        var bystanderInbox = await bystanderClient.GetFromJsonAsync<List<NotificationDto>>("/api/notifications/");
        Assert.DoesNotContain(bystanderInbox!, n => n.Title == "Job finished: test.kind");

        // Read lifecycle: mark it, and the unread view no longer returns it.
        (await recipientClient.PostAsync($"/api/notifications/{item.Id}/read", null)).EnsureSuccessStatusCode();
        var unreadAfter = await recipientClient.GetFromJsonAsync<List<NotificationDto>>("/api/notifications/?unreadOnly=true");
        Assert.DoesNotContain(unreadAfter!, n => n.Id == item.Id);
    }

    [Fact]
    public async Task MarkRead_OnSomeoneElsesNotification_Is404()
    {
        using var owner = fixture.ClientFor("notify_owner");
        using var intruder = fixture.ClientFor("notify_intruder");
        (await owner.GetAsync("/api/notifications/")).EnsureSuccessStatusCode();
        (await intruder.GetAsync("/api/notifications/")).EnsureSuccessStatusCode();

        Guid notificationId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var ownerUser = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == "it-notify_owner");
            var notifier = scope.ServiceProvider.GetRequiredService<INotifier>();
            await notifier.NotifyAsync(new Notification(
                ownerUser.TenantId, ownerUser.Id, "jobs", "Private note", "Owner-only."));
            notificationId = (await db.UserNotifications.IgnoreQueryFilters()
                .FirstAsync(n => n.UserId == ownerUser.Id && n.Title == "Private note")).Id;
        }

        using var response = await intruder.PostAsync($"/api/notifications/{notificationId}/read", null);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }
}

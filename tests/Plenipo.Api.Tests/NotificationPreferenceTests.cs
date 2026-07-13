using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Notifications;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// Per-user, per-category notification preferences: a member mutes one category (declared
/// manifest-first by a module) without losing everything else, and the mute suppresses the
/// notification entirely — in-app row and channels alike.
/// </summary>
public sealed class NotificationPreferenceTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public NotificationPreferenceTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task Preferences_list_the_manifest_declared_categories_defaulted_on()
    {
        using var client = ClientAs("prefs-reader");
        var prefs = await client.GetFromJsonAsync<JsonElement>("/api/notifications/preferences");

        var alert = prefs.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "test-alerts");
        Assert.Equal("Test alerts", alert.GetProperty("label").GetString());
        Assert.Equal("test", alert.GetProperty("moduleId").GetString());
        Assert.True(alert.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Muting_a_category_suppresses_the_notification_entirely()
    {
        using var client = ClientAs("prefs-muter");
        (await client.GetAsync("/api/platform/me")).EnsureSuccessStatusCode(); // provision the user

        // Resolve the provisioned user + tenant so the notifier can be driven directly, the way
        // a background producer (job processor, reminder sweep) drives it — no request scope.
        Guid tenantId, userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Subject == "prefs-muter");
            (tenantId, userId) = (user.TenantId, user.Id);
        }

        async Task NotifyAsync(string title)
        {
            using var scope = _factory.Services.CreateScope();
            var notifier = scope.ServiceProvider.GetRequiredService<INotifier>();
            await notifier.NotifyAsync(new Notification(tenantId, userId, "test-alerts", title, "body"));
        }

        await NotifyAsync("before mute");

        var mute = await client.PutAsJsonAsync("/api/notifications/preferences/test-alerts", new { enabled = false });
        mute.EnsureSuccessStatusCode();

        await NotifyAsync("while muted");

        (await client.PutAsJsonAsync("/api/notifications/preferences/test-alerts", new { enabled = true }))
            .EnsureSuccessStatusCode();

        await NotifyAsync("after unmute");

        var inbox = await client.GetFromJsonAsync<JsonElement>("/api/notifications/");
        var titles = inbox.EnumerateArray().Select(n => n.GetProperty("title").GetString()).ToList();
        Assert.Contains("before mute", titles);
        Assert.DoesNotContain("while muted", titles);
        Assert.Contains("after unmute", titles);
    }

    [Fact]
    public async Task A_category_no_module_declares_is_refused()
    {
        using var client = ClientAs("prefs-unknown");
        var response = await client.PutAsJsonAsync("/api/notifications/preferences/made-up", new { enabled = false });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

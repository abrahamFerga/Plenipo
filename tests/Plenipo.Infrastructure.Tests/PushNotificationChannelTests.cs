using Plenipo.Application.Notifications;
using Plenipo.Core.Multitenancy;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Notifications;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// The push channel: which devices a notification reaches, what content actually leaves the
/// deployment, and how a dead token gets cleaned up. Everything here runs against a recording
/// transport — no Expo account, no Apple or Google credentials, nothing to configure.
/// </summary>
public sealed class PushNotificationChannelTests
{
    private sealed class RecordingTransport(params PushDeliveryStatus[] statuses) : IPushTransport
    {
        public List<PushMessage> Sent { get; } = [];
        public int Calls { get; private set; }

        public Task<IReadOnlyList<PushResult>> SendAsync(
            IReadOnlyList<PushMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls++;
            Sent.AddRange(messages);
            // Default every token to delivered; a test opts specific positions into other outcomes.
            IReadOnlyList<PushResult> results = [.. messages.Select((m, i) =>
                new PushResult(m.Token, i < statuses.Length ? statuses[i] : PushDeliveryStatus.Delivered))];
            return Task.FromResult(results);
        }
    }

    /// <summary>
    /// No ambient tenant, on purpose: notifications are produced by jobs and webhooks outside any
    /// request scope, so the channel has to scope by the notification's own tenant id rather than
    /// leaning on the global query filter.
    /// </summary>
    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
        public bool HasTenant => false;
        public Guid RequireTenantId() => throw new InvalidOperationException("No tenant in this test.");
    }

    private static PlatformDbContext NewDb() => new(
        new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"push-{Guid.NewGuid()}").Options,
        new NoTenant());

    private static UserDevice Device(Guid tenantId, Guid userId, string token, string installation) => new()
    {
        TenantId = tenantId,
        UserId = userId,
        PushToken = token,
        InstallationId = installation,
        Platform = DevicePlatform.Ios,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    private static PushNotificationChannel Channel(
        PlatformDbContext db, IPushTransport transport, PushOptions? options = null) =>
        new(db, transport, Options.Create(options ?? new PushOptions()),
            NullLogger<PushNotificationChannel>.Instance);

    private static Notification Notify(Guid tenantId, Guid userId) => new(
        tenantId, userId, "legal.deadlines", "Filing due Friday", "Motion to dismiss — Ramirez v. Ortega.",
        "/legal/matters/42");

    [Fact]
    public async Task Reaches_every_device_the_recipient_registered()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.UserDevices.AddRange(
            Device(tenantId, userId, "token-phone", "install-phone"),
            Device(tenantId, userId, "token-tablet", "install-tablet"));
        await db.SaveChangesAsync();

        var transport = new RecordingTransport();
        await Channel(db, transport).SendAsync(Notify(tenantId, userId));

        Assert.Equal(1, transport.Calls); // one batched call, not one per device
        Assert.Equal(["token-phone", "token-tablet"], transport.Sent.Select(m => m.Token).Order());
    }

    [Fact]
    public async Task Carries_the_notification_content_and_its_deep_link()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.UserDevices.Add(Device(tenantId, userId, "token-1", "install-1"));
        await db.SaveChangesAsync();

        var transport = new RecordingTransport();
        await Channel(db, transport).SendAsync(Notify(tenantId, userId));

        var message = Assert.Single(transport.Sent);
        Assert.Equal("Filing due Friday", message.Title);
        Assert.Equal("Motion to dismiss — Ramirez v. Ortega.", message.Body);
        // The link is what lets a tap land on the record instead of the home tab.
        Assert.Equal("/legal/matters/42", message.Link);
        Assert.Equal("legal.deadlines", message.Category);
    }

    [Fact]
    public async Task Withholds_the_content_from_the_push_service_when_the_deployment_says_so()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.UserDevices.Add(Device(tenantId, userId, "token-1", "install-1"));
        await db.SaveChangesAsync();

        var transport = new RecordingTransport();
        var options = new PushOptions { IncludeContent = false };
        await Channel(db, transport, options).SendAsync(Notify(tenantId, userId));

        var message = Assert.Single(transport.Sent);
        Assert.Equal(options.PlaceholderTitle, message.Title);
        Assert.Equal(options.PlaceholderBody, message.Body);
        // The point of the switch: privileged material must not reach a third party or a lock screen.
        Assert.DoesNotContain("Ramirez", message.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("Ramirez", message.Body, StringComparison.Ordinal);
        // The category and link still travel — they're routing, not content.
        Assert.Equal("/legal/matters/42", message.Link);
    }

    [Fact]
    public async Task Never_crosses_a_tenant_or_a_user_boundary()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.UserDevices.AddRange(
            Device(tenantId, userId, "mine", "install-mine"),
            // Same user id in a different tenant, and a different user in the same tenant: the
            // notifier bypasses the query filter, so this is the channel's own scoping under test.
            Device(Guid.NewGuid(), userId, "other-tenant", "install-x"),
            Device(tenantId, Guid.NewGuid(), "other-user", "install-y"));
        await db.SaveChangesAsync();

        var transport = new RecordingTransport();
        await Channel(db, transport).SendAsync(Notify(tenantId, userId));

        Assert.Equal(["mine"], transport.Sent.Select(m => m.Token));
    }

    [Fact]
    public async Task Forgets_a_token_the_push_service_calls_permanently_gone()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.UserDevices.AddRange(
            Device(tenantId, userId, "dead", "install-dead"),
            Device(tenantId, userId, "alive", "install-alive"));
        await db.SaveChangesAsync();

        // First token gone, second delivered — the order the recording transport replies in.
        var transport = new RecordingTransport(PushDeliveryStatus.TokenGone);
        await Channel(db, transport).SendAsync(Notify(tenantId, userId));

        var remaining = await db.UserDevices.IgnoreQueryFilters().Select(d => d.PushToken).ToListAsync();
        Assert.Equal(["alive"], remaining);
    }

    [Fact]
    public async Task Keeps_a_device_whose_delivery_merely_failed()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.UserDevices.Add(Device(tenantId, userId, "flaky", "install-flaky"));
        await db.SaveChangesAsync();

        // A rate limit or a provider hiccup says nothing about whether the device still exists.
        var transport = new RecordingTransport(PushDeliveryStatus.Failed);
        await Channel(db, transport).SendAsync(Notify(tenantId, userId));

        Assert.Single(await db.UserDevices.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Costs_nothing_when_the_recipient_has_no_devices()
    {
        await using var db = NewDb();
        var transport = new RecordingTransport();

        await Channel(db, transport).SendAsync(Notify(Guid.NewGuid(), Guid.NewGuid()));

        // The whole reason the channel can be registered unconditionally: a deployment with no
        // mobile app never touches a push service.
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Sends_nothing_when_an_operator_switches_push_off()
    {
        await using var db = NewDb();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.UserDevices.Add(Device(tenantId, userId, "token-1", "install-1"));
        await db.SaveChangesAsync();

        var transport = new RecordingTransport();
        await Channel(db, transport, new PushOptions { Enabled = false }).SendAsync(Notify(tenantId, userId));

        Assert.Equal(0, transport.Calls);
        // The kill switch is push-specific: the in-app row is the notifier's job and is untouched.
        Assert.Single(await db.UserDevices.IgnoreQueryFilters().ToListAsync());
    }
}

using Cortex.Application.Notifications;
using Cortex.Core.Multitenancy;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Notifications;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cortex.Infrastructure.Tests;

/// <summary>
/// The email notification channel delivers to real mailboxes only: any channel-synthesized
/// <c>{externalId}@{channelId}.channel</c> address (WhatsApp phones, email-intake correspondents,
/// future adapters) is skipped — those users are reached through their channel, not by
/// notification mail to an unroutable address.
/// </summary>
public sealed class EmailNotificationChannelTests
{
    private sealed class CapturingSmtp : ISmtpTransport
    {
        public List<EmailMessage> Sent { get; } = [];
        public bool IsConfigured => true;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
        public bool HasTenant => false;
        public Guid RequireTenantId() => throw new InvalidOperationException("No tenant in this test.");
    }

    private static readonly EmailOptions Configured = new()
    {
        Enabled = true,
        Host = "smtp.example.test",
        FromAddress = "noreply@example.test",
    };

    [Theory]
    [InlineData("lawyer@firm.test", true)]
    [InlineData("5215550100@whatsapp.channel", false)]
    [InlineData("client@example.com@email.channel", false)]
    [InlineData("someone@telegram.channel", false)]
    public async Task Delivers_to_real_mailboxes_and_skips_channel_synthesized_addresses(
        string email, bool expectDelivery)
    {
        await using var db = new PlatformDbContext(
            new DbContextOptionsBuilder<PlatformDbContext>()
                .UseInMemoryDatabase($"notify-{Guid.NewGuid()}").Options,
            new NoTenant());

        var tenantId = Guid.NewGuid();
        var user = new User
        {
            TenantId = tenantId,
            Subject = "subject-1",
            Email = email,
            DisplayName = "Recipient",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var smtp = new CapturingSmtp();
        var channel = new EmailNotificationChannel(smtp, Options.Create(Configured), db);

        await channel.SendAsync(new Notification(
            tenantId, user.Id, "approvals", "An action needs approval", "Body text", Link: null));

        if (expectDelivery)
        {
            var sent = Assert.Single(smtp.Sent);
            Assert.Equal(email, sent.To);
            Assert.Equal("An action needs approval", sent.Subject);
        }
        else
        {
            Assert.Empty(smtp.Sent);
        }
    }

    [Fact]
    public async Task Stays_silent_until_the_email_section_is_configured()
    {
        await using var db = new PlatformDbContext(
            new DbContextOptionsBuilder<PlatformDbContext>()
                .UseInMemoryDatabase($"notify-{Guid.NewGuid()}").Options,
            new NoTenant());

        var smtp = new CapturingSmtp();
        var channel = new EmailNotificationChannel(smtp, Options.Create(new EmailOptions()), db);

        await channel.SendAsync(new Notification(
            Guid.NewGuid(), Guid.NewGuid(), "approvals", "Title", "Body", Link: null));

        Assert.Empty(smtp.Sent);
    }
}

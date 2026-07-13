using Cortex.Application.Channels;
using Cortex.Application.Notifications;
using Cortex.Infrastructure.Channels;
using Cortex.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// The email-intake channel, keyless: a fake IMAP inbox feeds canned mail and a capturing fake
/// stands in for SMTP, while everything between them is the real pipeline — the channel-agnostic
/// turn core (JIT email:{address} identity, seat gate, permissions), attachment storage, the Mock
/// agent turn on a stable per-sender conversation, watermark persistence, and the reply.
/// </summary>
[Collection("api")]
public sealed class EmailIntakeChannelTests : IDisposable
{
    private sealed class FakeImapInbox : IImapInbox
    {
        public List<InboundEmail> Pending { get; } = [];
        public string? NextWatermark { get; set; }
        public List<string?> SeenWatermarks { get; } = [];

        public Task<ImapPollResult> FetchNewAsync(string? watermark, CancellationToken cancellationToken = default)
        {
            SeenWatermarks.Add(watermark);
            var batch = Pending.ToList();
            Pending.Clear();
            return Task.FromResult(new ImapPollResult(batch, NextWatermark));
        }
    }

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

    private readonly FakeImapInbox _inbox = new();
    private readonly CapturingSmtp _smtp = new();
    private readonly WebApplicationFactory<Program> _factory;

    public EmailIntakeChannelTests(IntegrationFixture fixture)
    {
        _factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Channels:Email:Enabled", "true");
            builder.UseSetting("Channels:Email:Host", "imap.example.test");
            builder.UseSetting("Channels:Email:Username", "intake@firm.test");
            builder.UseSetting("Channels:Email:Password", "it-password");
            builder.UseSetting("Channels:Email:ModuleId", "finance");
            builder.UseSetting("Channels:Email:TenantSlug", "dev");
            builder.UseSetting("Channels:Email:ReplyEnabled", "true");
            builder.UseSetting("Channels:Email:AllowUnknownSenders", "true");
            builder.UseSetting("Channels:Email:PollSeconds", "3600"); // the poller stays out of the way; tests drive polls
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IImapInbox>(_inbox);
                services.AddSingleton<ISmtpTransport>(_smtp);
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task New_mail_runs_a_turn_stores_the_attachment_and_replies()
    {
        _inbox.Pending.Add(new InboundEmail(
            MessageId: "mid-1",
            FromAddress: "Ada.Client@Example.com",
            FromName: "Ada Client",
            Subject: "Engagement question",
            TextBody: "How much did I spend on groceries?",
            Attachments: [new InboundEmailAttachment("retainer.txt", "text/plain", "Retainer draft."u8.ToArray())]));
        _inbox.NextWatermark = "7:42";

        using var scope = _factory.Services.CreateScope();
        var processed = await scope.ServiceProvider.GetRequiredService<EmailChannelService>().PollOnceAsync();
        Assert.Equal(1, processed);

        // The correspondent is a real JIT-provisioned user under the channel's identity scheme.
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Subject == "email:ada.client@example.com");
        Assert.NotNull(user);
        Assert.Equal("Ada Client", user!.DisplayName);

        // The attachment landed in the tenant file store, stamped with the channel as its source.
        var stored = await db.StoredFiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.FileName == "retainer.txt" && f.Source == "email");
        Assert.NotNull(stored);

        // The turn is a persisted conversation on the stable per-sender id: subject + body + file block.
        var conversation = await db.Conversations.IgnoreQueryFilters().Include(c => c.Messages)
            .FirstAsync(c => c.Id == ChannelTurnService.ConversationIdFor(tenant.Id, "email:ada.client@example.com"));
        Assert.Equal(user.Id, conversation.UserId);
        Assert.Contains(conversation.Messages, m =>
            m.Content.Contains("groceries", StringComparison.OrdinalIgnoreCase) &&
            m.Content.Contains("Subject: Engagement question", StringComparison.Ordinal) &&
            m.Content.Contains("[Attached files]", StringComparison.Ordinal));

        // The agent's answer went back out over SMTP with reply threading.
        var reply = Assert.Single(_smtp.Sent);
        Assert.Equal("Ada.Client@Example.com", reply.To);
        Assert.Equal("Re: Engagement question", reply.Subject);
        Assert.False(string.IsNullOrWhiteSpace(reply.TextBody));

        // The watermark persisted, so a restart never re-reads this mail.
        var cursor = await db.ChannelCursors.FindAsync(EmailChannelService.ChannelId);
        Assert.Equal("7:42", cursor!.Watermark);
    }

    [Fact]
    public async Task The_next_poll_resumes_from_the_saved_watermark()
    {
        using var scope = _factory.Services.CreateScope();
        var channel = scope.ServiceProvider.GetRequiredService<EmailChannelService>();

        _inbox.NextWatermark = "9:10";
        await channel.PollOnceAsync();

        _inbox.NextWatermark = "9:12";
        await channel.PollOnceAsync();

        Assert.Equal("9:10", _inbox.SeenWatermarks[^1]);
    }

    [Fact]
    public async Task Mail_from_the_intake_address_itself_is_never_answered()
    {
        _inbox.Pending.Add(new InboundEmail(
            "mid-self", "INTAKE@firm.test", null, "Auto-reply", "I am the mailbox.", []));
        _inbox.NextWatermark = "11:1";

        using var scope = _factory.Services.CreateScope();
        var before = _smtp.Sent.Count;
        var processed = await scope.ServiceProvider.GetRequiredService<EmailChannelService>().PollOnceAsync();

        Assert.Equal(0, processed);
        Assert.Equal(before, _smtp.Sent.Count);

        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Subject == "email:intake@firm.test"));
    }
}

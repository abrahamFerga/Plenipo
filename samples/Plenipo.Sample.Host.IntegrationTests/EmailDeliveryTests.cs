using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Plenipo.Application.Notifications;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Email delivery (improvement loop it3): the SMTP transport seam feeds BOTH the notification
/// fan-out channel and product hooks — a provisioned tenant's admin gets the welcome email
/// (WelcomeEmailHook exercising ITenantProvisionedHook + ISmtpTransport together), and a platform
/// notification reaches the recipient's real mailbox. Keyless via a recording transport.
/// </summary>
[Collection("api")]
public sealed class EmailDeliveryTests : IDisposable
{
    private sealed class RecordingSmtp : ISmtpTransport
    {
        public ConcurrentQueue<EmailMessage> Sent { get; } = new();

        public bool IsConfigured => true;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private readonly RecordingSmtp _smtp = new();
    private readonly WebApplicationFactory<Program> _factory;

    public EmailDeliveryTests(IntegrationFixture fixture)
    {
        _factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Email:Enabled", "true");
            b.UseSetting("Email:Host", "smtp.test.invalid");
            b.UseSetting("Email:FromAddress", "noreply@test.invalid");
            b.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<ISmtpTransport>(_smtp)));
        });
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ProvisionedTenantAdmin_GetsTheWelcomeEmail()
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Dev-Subject", "it-system_admin");
        admin.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        admin.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");

        using var created = await admin.PostAsJsonAsync("/api/admin/tenants/provision", new
        {
            name = "Welcome Mail LLP",
            slug = "welcome-mail",
            adminEmail = "partner@welcomemail.example",
            modules = new[] { "legal" },
            maxSeats = 2,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var mail = Assert.Single(_smtp.Sent);
        Assert.Equal("partner@welcomemail.example", mail.To);
        Assert.Contains("Welcome Mail LLP", mail.Subject);
        Assert.Contains("welcome-mail", mail.TextBody);
        Assert.Contains("legal", mail.TextBody);
        Assert.Contains("Seats: 2", mail.TextBody);
    }

    [Fact]
    public async Task PlatformNotification_ReachesTheRecipientsMailbox()
    {
        // A user with a real mailbox exists (JIT).
        var user = _factory.CreateClient();
        user.DefaultRequestHeaders.Add("X-Dev-Subject", "email-notify-user");
        user.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        user.DefaultRequestHeaders.Add("X-Dev-Roles", "user");
        user.DefaultRequestHeaders.Add("X-Dev-Email", "notify-me@example.test");
        (await user.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();

        // Fire a platform notification through the real notifier (as the job processor would).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var recipient = await db.Users.IgnoreQueryFilters()
                .FirstAsync(u => u.Subject == "email-notify-user");
            var notifier = scope.ServiceProvider.GetRequiredService<INotifier>();
            await notifier.NotifyAsync(new Notification(
                recipient.TenantId, recipient.Id, "reminder", "Deadline tomorrow",
                "Answer due on Vandelay acquisition.", "/legal/deadlines"));
        }

        var mail = Assert.Single(_smtp.Sent, m => m.To == "notify-me@example.test");
        Assert.Equal("Deadline tomorrow", mail.Subject);
        Assert.Contains("Vandelay", mail.TextBody);
        Assert.Contains("/legal/deadlines", mail.TextBody);
    }
}

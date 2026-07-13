using System.Net;
using System.Net.Mail;
using Plenipo.Application.Notifications;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Notifications;

/// <summary>
/// The default <see cref="ISmtpTransport"/>: plain SMTP with STARTTLS via the framework's
/// SmtpClient — dependency-free and fine for transactional volumes. Growing needs (DKIM, OAuth
/// SMTP, provider APIs) swap this for a MailKit- or API-based transport with one registration;
/// everything that COMPOSES email depends only on the seam.
/// </summary>
public sealed class SmtpClientTransport(IOptions<EmailOptions> options) : ISmtpTransport
{
    public bool IsConfigured => options.Value.IsEnabled;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        if (!opts.IsEnabled)
        {
            throw new InvalidOperationException("Email delivery is not configured (the \"Email\" section).");
        }

        using var client = new SmtpClient(opts.Host, opts.Port)
        {
            EnableSsl = opts.UseStartTls,
            Credentials = string.IsNullOrWhiteSpace(opts.Username)
                ? null
                : new NetworkCredential(opts.Username, opts.Password),
        };

        using var mail = new MailMessage
        {
            From = new MailAddress(opts.FromAddress!, opts.FromName),
            Subject = message.Subject,
            Body = message.TextBody,
        };
        mail.To.Add(message.To);

        await client.SendMailAsync(mail, cancellationToken);
    }
}

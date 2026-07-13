namespace Plenipo.Application.Notifications;

/// <summary>One outbound email as the platform composes it.</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody);

/// <summary>
/// The SMTP seam: the email channel and product hooks compose messages; this sends them. The
/// default implementation is the framework's SmtpClient — swap in a MailKit- or API-based
/// transport with one DI registration when delivery needs grow.
/// </summary>
public interface ISmtpTransport
{
    public bool IsConfigured { get; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Email delivery configuration (the "Email" section). Off by default; the PASSWORD IS A SECRET
/// (user-secrets/Key Vault/environment — never appsettings).
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; set; }

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;

    public string? Username { get; set; }

    /// <summary>SECRET — never in appsettings.</summary>
    public string? Password { get; set; }

    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "Plenipo";

    public bool IsEnabled => Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

namespace Cortex.Application.Channels;

/// <summary>
/// Email-intake channel configuration, bound from "Channels:Email". Disabled by default; when
/// enabled the platform polls an org-owned IMAP mailbox (a firm's intake address) and turns each
/// new message — attachments included — into an authorized agent turn for the sender's
/// JIT-provisioned identity, exactly like WhatsApp turns (docs/INBOUND_CHANNELS.md). The mailbox
/// password comes from user-secrets / Key Vault, never source.
/// </summary>
public sealed class EmailChannelOptions
{
    public const string SectionName = "Channels:Email";

    public bool Enabled { get; set; }

    /// <summary>IMAP server host, e.g. "imap.fastmail.com".</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 993;

    public bool UseSsl { get; set; } = true;

    /// <summary>The mailbox login — also the intake address; mail FROM this address is skipped.</summary>
    public string? Username { get; set; }

    /// <summary>The mailbox password or app password (user-secrets / Key Vault).</summary>
    public string? Password { get; set; }

    /// <summary>The folder to watch.</summary>
    public string Folder { get; set; } = "INBOX";

    /// <summary>The module whose agent answers intake mail (e.g. "legal").</summary>
    public string? ModuleId { get; set; }

    /// <summary>The tenant slug correspondents are provisioned into.</summary>
    public string? TenantSlug { get; set; }

    /// <summary>Seconds between polls.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>
    /// When true, the agent's answer is mailed back to the sender through the platform's SMTP
    /// transport (the Email: section). Off by default — intake-only is the safe start.
    /// </summary>
    public bool ReplyEnabled { get; set; }

    /// <summary>Allow any From address to create a Cortex user. Off by default.</summary>
    public bool AllowUnknownSenders { get; set; }

    /// <summary>Email addresses allowed to JIT-provision when unknown-sender access is off.</summary>
    public string[] AllowedSenders { get; set; } = [];

    /// <summary>Maximum complete MIME message size downloaded from IMAP.</summary>
    public long MaxMessageBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Throws when the channel is enabled but missing a setting it cannot run without.</summary>
    public void ThrowIfInvalid()
    {
        if (!Enabled)
        {
            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Host)) missing.Add(nameof(Host));
        if (string.IsNullOrWhiteSpace(Username)) missing.Add(nameof(Username));
        if (string.IsNullOrWhiteSpace(Password)) missing.Add(nameof(Password));
        if (string.IsNullOrWhiteSpace(ModuleId)) missing.Add(nameof(ModuleId));
        if (string.IsNullOrWhiteSpace(TenantSlug)) missing.Add(nameof(TenantSlug));

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"The email-intake channel is enabled but misconfigured — missing: {string.Join(", ", missing)}. " +
                $"Set Channels:Email:* (the password via user-secrets or Key Vault) or set Channels:Email:Enabled=false.");
        }
    }
}

/// <summary>One attachment of an inbound email, fully buffered (intake mail is small; simplicity wins).</summary>
public sealed record InboundEmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>One new message from the watched mailbox, as the channel needs it.</summary>
public sealed record InboundEmail(
    string MessageId,
    string FromAddress,
    string? FromName,
    string? Subject,
    string? TextBody,
    IReadOnlyList<InboundEmailAttachment> Attachments);

/// <summary>
/// One poll of the mailbox. <paramref name="NextWatermark"/> is the "UIDVALIDITY:lastUID" pair to
/// pass into the next poll — messages at or below it are never returned again, and a changed
/// UIDVALIDITY (rebuilt mailbox) restarts cleanly from the folder's current state.
/// </summary>
public sealed record ImapPollResult(IReadOnlyList<InboundEmail> Messages, string? NextWatermark);

/// <summary>
/// The slice of IMAP the channel needs — a seam so keyless tests feed canned messages while
/// production speaks MailKit. Implementations connect per poll; no session is held between polls.
/// </summary>
public interface IImapInbox
{
    public Task<ImapPollResult> FetchNewAsync(string? watermark, CancellationToken cancellationToken = default);
}

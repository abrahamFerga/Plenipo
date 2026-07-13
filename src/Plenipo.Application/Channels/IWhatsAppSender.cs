namespace Plenipo.Application.Channels;

/// <summary>
/// Sends outbound WhatsApp messages. The production implementation calls the Meta Cloud API; tests
/// substitute a capturing fake so the whole channel is exercisable with no Meta account or API key.
/// </summary>
public interface IWhatsAppSender
{
    /// <summary>Sends a text message to a phone number in E.164-without-plus form (as Meta delivers it).</summary>
    public Task SendTextAsync(string to, string text, CancellationToken cancellationToken = default);
}

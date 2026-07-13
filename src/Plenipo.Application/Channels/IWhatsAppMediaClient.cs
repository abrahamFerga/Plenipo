namespace Plenipo.Application.Channels;

/// <summary>A media object downloaded from the WhatsApp Cloud API.</summary>
public sealed record WhatsAppMedia(Stream Content, string ContentType) : IAsyncDisposable
{
    public async ValueTask DisposeAsync() => await Content.DisposeAsync();
}

/// <summary>
/// Downloads inbound media (documents, images) referenced by WhatsApp webhook deliveries. The
/// production implementation calls the Meta Cloud API's two-step media endpoint; tests substitute a
/// fake so media flows are E2E-testable with no Meta account.
/// </summary>
public interface IWhatsAppMediaClient
{
    /// <summary>Downloads a media object by its Cloud API media id. Null when it can't be retrieved.</summary>
    public Task<WhatsAppMedia?> DownloadAsync(string mediaId, CancellationToken cancellationToken = default);
}

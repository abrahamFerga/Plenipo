using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Cortex.Application.Security;

/// <summary>Operator-owned policy for tenant-configurable outbound HTTP destinations.</summary>
public sealed class OutboundUrlOptions
{
    public const string SectionName = "Security:OutboundUrls";

    /// <summary>Permit plain HTTP. Off by default so credentials cannot be sent without TLS.</summary>
    public bool AllowHttp { get; set; }

    /// <summary>
    /// Permit loopback, link-local, and private address space. Off by default; enable only for a
    /// deliberately isolated deployment that must reach operator-controlled internal services.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
}

/// <summary>
/// Rejects tenant-controlled URLs that could reach the host, cloud metadata, or internal services.
/// DNS is resolved immediately before each request; callers also disable automatic redirects so a
/// public endpoint cannot redirect the HTTP handler around this check.
/// </summary>
public sealed class OutboundUrlPolicy(IOptions<OutboundUrlOptions> options)
{
    public async Task<Uri> RequireAllowedAsync(string value, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("URL must be an absolute HTTP(S) URL.", nameof(value));
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !options.Value.AllowHttp)
        {
            throw new ArgumentException("Plain HTTP destinations are disabled by the deployment operator.", nameof(value));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("URLs containing user-info credentials are not allowed.", nameof(value));
        }

        if (options.Value.AllowPrivateNetworks)
        {
            return uri;
        }

        if (uri.IsLoopback || string.Equals(uri.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.DnsSafeHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Loopback destinations are not allowed.", nameof(value));
        }

        await ResolveAllowedAddressesAsync(uri.DnsSafeHost, cancellationToken);
        return uri;
    }

    /// <summary>
    /// Creates a redirect-free handler that validates the DNS answer used for the actual socket.
    /// This closes the resolve/check/connect gap that otherwise permits DNS rebinding after URL validation.
    /// </summary>
    public SocketsHttpHandler CreateHttpMessageHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = ConnectAllowedAsync,
    };

    private async ValueTask<Stream> ConnectAllowedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
                if (ex is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException("Unable to connect to an allowed destination address.", lastError);
    }

    private async Task<IPAddress[]> ResolveAllowedAddressesAsync(
        string host,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            throw new ArgumentException("The destination host could not be resolved.", nameof(host), ex);
        }

        if (addresses.Length == 0 ||
            (!options.Value.AllowPrivateNetworks && addresses.Any(IsPrivateOrSpecial)))
        {
            throw new ArgumentException("Private, link-local, multicast, and special-purpose destinations are not allowed.", nameof(host));
        }

        return addresses;
    }

    private static bool IsPrivateOrSpecial(IPAddress input)
    {
        var address = input.IsIPv4MappedToIPv6 ? input.MapToIPv4() : input;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] is 0 or 10 or 127 ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   bytes[0] >= 224;
        }

        // IPv6: multicast ff00::/8, link-local fe80::/10, and unique-local fc00::/7.
        return bytes[0] == 0xff ||
               (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
               (bytes[0] & 0xfe) == 0xfc;
    }
}

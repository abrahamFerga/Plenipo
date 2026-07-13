using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Plenipo.AspNetCore.Channels;

/// <summary>
/// Verifies Meta's <c>X-Hub-Signature-256</c> webhook signature: an HMAC-SHA256 of the raw request
/// body keyed with the app secret, sent as <c>sha256=&lt;lowercase hex&gt;</c>. The webhook endpoint is
/// anonymous by necessity, so this signature is its only authentication — comparison is constant-time.
/// </summary>
public static class WhatsAppSignature
{
    private const string Prefix = "sha256=";

    public static bool IsValid(ReadOnlySpan<byte> body, string? signatureHeader, string appSecret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) ||
            !signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Span<byte> presented = stackalloc byte[32];
        var status = Convert.FromHexString(signatureHeader.AsSpan(Prefix.Length), presented, out _, out var written);
        if (status != OperationStatus.Done || written != presented.Length)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body, expected);

        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    /// <summary>Computes the header value for a body — used by tests and local tooling to sign requests.</summary>
    public static string Compute(ReadOnlySpan<byte> body, string appSecret)
    {
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body, hash);
        return Prefix + Convert.ToHexStringLower(hash);
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Plenipo.AspNetCore.Commerce;

/// <summary>
/// Verifies Stripe's <c>Stripe-Signature</c> webhook header without the Stripe SDK: the header
/// carries <c>t=&lt;unix&gt;,v1=&lt;hex&gt;[,v1=…]</c>, and a valid v1 is the HMAC-SHA256 of
/// <c>"{t}.{raw body}"</c> keyed with the endpoint's signing secret. The endpoint is anonymous by
/// necessity, so this signature is its only authentication — comparison is constant-time, and the
/// timestamp bounds replay. Scheme: https://docs.stripe.com/webhooks#verify-manually.
/// </summary>
public static class StripeSignature
{
    public static bool IsValid(
        ReadOnlySpan<byte> body, string? signatureHeader, string secret,
        DateTimeOffset now, int toleranceSeconds)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        long? timestamp = null;
        var candidates = new List<string>(2);
        foreach (var part in signatureHeader.Split(','))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2)
            {
                continue;
            }

            if (kv[0] == "t" && long.TryParse(kv[1], out var t))
            {
                timestamp = t;
            }
            else if (kv[0] == "v1")
            {
                candidates.Add(kv[1]);
            }
        }

        if (timestamp is not { } ts || candidates.Count == 0)
        {
            return false;
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - ts) > toleranceSeconds)
        {
            return false; // replay window exceeded
        }

        // signed_payload = "{t}." + raw body bytes (never a re-serialized model).
        var prefix = Encoding.ASCII.GetBytes($"{ts}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));

        Span<byte> expected = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed, expected);

        Span<byte> presented = stackalloc byte[32];
        foreach (var candidate in candidates)
        {
            if (candidate.Length == 64 &&
                Convert.FromHexString(candidate, presented, out _, out var written) == System.Buffers.OperationStatus.Done &&
                written == 32 &&
                CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Computes a valid header for a body — used by tests and local tooling to sign requests.</summary>
    public static string Compute(ReadOnlySpan<byte> body, string secret, DateTimeOffset now)
    {
        var ts = now.ToUnixTimeSeconds();
        var prefix = Encoding.ASCII.GetBytes($"{ts}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));

        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed, hash);
        return $"t={ts},v1={Convert.ToHexStringLower(hash)}";
    }
}

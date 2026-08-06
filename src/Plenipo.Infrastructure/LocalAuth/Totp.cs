using System.Security.Cryptography;
using System.Text;

namespace Plenipo.Infrastructure.LocalAuth;

/// <summary>
/// RFC 6238 TOTP (SHA-1, 30-second step, 6 digits — the parameters every authenticator app ships
/// with) plus the RFC 4648 base32 codec the <c>otpauth://</c> URI format requires. Implemented
/// in-repo rather than taken as a package for the same reason the SPA hand-rolls PKCE: it is a small,
/// vector-testable algorithm, and a dependency inherits every consumer's audit surface. Verified
/// against the RFC 6238 Appendix B test vectors in Plenipo.Infrastructure.Tests.
/// </summary>
public static class Totp
{
    private const int StepSeconds = 30;
    private const int Digits = 6;

    /// <summary>Verification accepts the current step ±1 to absorb clock drift and typing time.</summary>
    private const int DriftSteps = 1;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>A new 160-bit secret, base32-encoded — the size RFC 4226 recommends for HMAC-SHA-1.</summary>
    public static string GenerateSecret() => ToBase32(RandomNumberGenerator.GetBytes(20));

    /// <summary>
    /// The enrollment URI an authenticator app consumes (tapped on mobile, or the secret typed
    /// manually on desktop — Plenipo deliberately renders no QR image, see ADR 0003).
    /// </summary>
    public static string BuildOtpAuthUri(string issuer, string account, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    /// <summary>True when <paramref name="code"/> matches the secret at <paramref name="at"/> ± one step.</summary>
    public static bool Verify(string base32Secret, string code, DateTimeOffset at)
    {
        var trimmed = code.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (trimmed.Length != Digits || !trimmed.All(char.IsAsciiDigit))
        {
            return false;
        }

        var key = FromBase32(base32Secret);
        var step = at.ToUnixTimeSeconds() / StepSeconds;
        for (var drift = -DriftSteps; drift <= DriftSteps; drift++)
        {
            // Fixed-time comparison: a timing oracle over six digits is small, but free to close.
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(ComputeCode(key, step + drift)),
                    Encoding.ASCII.GetBytes(trimmed)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>RFC 4226 HOTP for one counter value — exposed for the vector tests.</summary>
    public static string ComputeCode(byte[] key, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        Span<byte> hash = stackalloc byte[20];
        // HMAC-SHA-1 is what RFC 6238/4226 specify and what every authenticator app implements; the
        // secret is 160 bits and single-purpose, and SHA-1's collision weakness is irrelevant to HMAC
        // here. Using anything stronger would simply fail to interoperate.
#pragma warning disable CA5350
        using (var hmac = new HMACSHA1(key))
#pragma warning restore CA5350
        {
            hmac.TryComputeHash(counterBytes, hash, out _);
        }

        // RFC 4226 §5.3 dynamic truncation: low nibble of the last byte picks a 4-byte window.
        var offset = hash[19] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string ToBase32(byte[] bytes)
    {
        var result = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Base32Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            result.Append(Base32Alphabet[(buffer << (5 - bits)) & 0x1F]);
        }

        return result.ToString();
    }

    public static byte[] FromBase32(string encoded)
    {
        var clean = encoded.Trim().TrimEnd('=').ToUpperInvariant();
        var result = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var index = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new FormatException("Value is not valid base32.");
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. result];
    }
}

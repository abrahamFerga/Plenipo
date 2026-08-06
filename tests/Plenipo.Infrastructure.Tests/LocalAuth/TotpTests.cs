using System.Text;
using Plenipo.Infrastructure.LocalAuth;
using Xunit;

namespace Plenipo.Infrastructure.Tests.LocalAuth;

/// <summary>
/// The in-repo TOTP implementation, pinned to the RFCs it claims: HOTP dynamic truncation against
/// the RFC 4226 Appendix D vectors (6 digits — what authenticator apps show), and base32 against the
/// RFC 4648 §10 vectors. If these pass, any standards-conforming authenticator app agrees with us.
/// </summary>
public sealed class TotpTests
{
    private static readonly byte[] Rfc4226Secret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(0, "755224")]
    [InlineData(1, "287082")]
    [InlineData(2, "359152")]
    [InlineData(3, "969429")]
    [InlineData(4, "338314")]
    [InlineData(5, "254676")]
    [InlineData(6, "287922")]
    [InlineData(7, "162583")]
    [InlineData(8, "399871")]
    [InlineData(9, "520489")]
    public void Matches_the_rfc4226_hotp_vectors(long counter, string expected) =>
        Assert.Equal(expected, Totp.ComputeCode(Rfc4226Secret, counter));

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Matches_the_rfc4648_base32_vectors(string ascii, string encoded)
    {
        Assert.Equal(encoded, Totp.ToBase32(Encoding.ASCII.GetBytes(ascii)));
        Assert.Equal(ascii, Encoding.ASCII.GetString(Totp.FromBase32(encoded)));
    }

    [Fact]
    public void Generated_secrets_round_trip_and_are_160_bits()
    {
        var secret = Totp.GenerateSecret();
        Assert.Equal(20, Totp.FromBase32(secret).Length);
        Assert.Equal(secret, Totp.ToBase32(Totp.FromBase32(secret)));
    }

    [Fact]
    public void Verify_accepts_the_current_and_adjacent_steps_only()
    {
        var secret = Totp.GenerateSecret();
        var key = Totp.FromBase32(secret);
        var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_015); // mid-step, drift both ways
        var step = at.ToUnixTimeSeconds() / 30;

        Assert.True(Totp.Verify(secret, Totp.ComputeCode(key, step), at));
        Assert.True(Totp.Verify(secret, Totp.ComputeCode(key, step - 1), at));
        Assert.True(Totp.Verify(secret, Totp.ComputeCode(key, step + 1), at));
        Assert.False(Totp.Verify(secret, Totp.ComputeCode(key, step - 2), at));
        Assert.False(Totp.Verify(secret, Totp.ComputeCode(key, step + 2), at));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    public void Verify_rejects_malformed_codes(string code) =>
        Assert.False(Totp.Verify(Totp.GenerateSecret(), code, DateTimeOffset.UtcNow));

    [Fact]
    public void Verify_tolerates_spaces_as_authenticator_apps_display_them()
    {
        var secret = Totp.GenerateSecret();
        var at = DateTimeOffset.UtcNow;
        var code = Totp.ComputeCode(Totp.FromBase32(secret), at.ToUnixTimeSeconds() / 30);

        Assert.True(Totp.Verify(secret, $"{code[..3]} {code[3..]}", at));
    }

    [Fact]
    public void OtpAuth_uri_escapes_issuer_and_account()
    {
        var uri = Totp.BuildOtpAuthUri("Family Office", "user@example.test", "ABC234");

        Assert.StartsWith("otpauth://totp/Family%20Office:user%40example.test?secret=ABC234", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Family%20Office", uri, StringComparison.Ordinal);
        Assert.Contains("digits=6", uri, StringComparison.Ordinal);
        Assert.Contains("period=30", uri, StringComparison.Ordinal);
    }
}

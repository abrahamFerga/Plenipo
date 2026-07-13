using System.Text;
using Plenipo.AspNetCore.Commerce;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The webhook's only authentication: Stripe's t=…,v1=… HMAC scheme over "{t}.{raw body}" with a
/// replay window. Pure function, exhaustively checkable without Stripe.
/// </summary>
public sealed class StripeSignatureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_780_000_000);
    private const string Secret = "whsec_test_secret";

    private static byte[] Body(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void RoundTrip_ComputeThenVerify_IsValid()
    {
        var body = Body("""{"id":"evt_1","type":"checkout.session.completed"}""");
        var header = StripeSignature.Compute(body, Secret, Now);

        Assert.True(StripeSignature.IsValid(body, header, Secret, Now, 300));
    }

    [Fact]
    public void TamperedBody_IsRejected()
    {
        var body = Body("""{"id":"evt_1"}""");
        var header = StripeSignature.Compute(body, Secret, Now);

        Assert.False(StripeSignature.IsValid(Body("""{"id":"evt_2"}"""), header, Secret, Now, 300));
    }

    [Fact]
    public void WrongSecret_IsRejected()
    {
        var body = Body("{}");
        var header = StripeSignature.Compute(body, "whsec_other", Now);

        Assert.False(StripeSignature.IsValid(body, header, Secret, Now, 300));
    }

    [Fact]
    public void OutsideTheReplayWindow_IsRejected()
    {
        var body = Body("{}");
        var header = StripeSignature.Compute(body, Secret, Now);

        Assert.False(StripeSignature.IsValid(body, header, Secret, Now.AddSeconds(301), 300));
        Assert.True(StripeSignature.IsValid(body, header, Secret, Now.AddSeconds(299), 300));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=deadbeef")]                 // no timestamp
    [InlineData("t=1780000000")]                // no signature
    [InlineData("t=notanumber,v1=deadbeef")]
    [InlineData("t=1780000000,v1=tooshort")]
    public void MalformedHeaders_AreRejected(string? header)
    {
        Assert.False(StripeSignature.IsValid(Body("{}"), header, Secret, Now, 300));
    }

    [Fact]
    public void ExtraV1Candidates_AnyValidOnePasses()
    {
        // Stripe sends multiple v1 entries during secret rotation.
        var body = Body("{}");
        var valid = StripeSignature.Compute(body, Secret, Now);
        var rotated = $"t={Now.ToUnixTimeSeconds()},v1={new string('0', 64)},{valid.Split(',')[1]}";

        Assert.True(StripeSignature.IsValid(body, rotated, Secret, Now, 300));
    }
}

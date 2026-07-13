using System.Security.Claims;
using Plenipo.AspNetCore.Auth;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The Auth:RequireMfa backstop's judgment: a validated token must carry an accepted amr value.
/// Enrollment lives at the IdP; these tests pin what the platform accepts as proof, across both
/// wire shapes an amr claim arrives in.
/// </summary>
public class MfaEnforcementTests
{
    private static readonly AuthOptions Defaults = new();

    private static ClaimsPrincipal PrincipalWith(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "test"));

    [Theory]
    [InlineData("mfa")]
    [InlineData("ngcmfa")]
    [InlineData("fido")]
    [InlineData("otp")]
    [InlineData("hwk")]
    [InlineData("MFA")] // markers compare case-insensitively
    public void Accepts_each_default_marker_as_its_own_claim(string marker)
    {
        var principal = PrincipalWith(new Claim("amr", "pwd"), new Claim("amr", marker));

        Assert.True(MfaEnforcement.SatisfiesMfa(principal, Defaults));
    }

    [Fact]
    public void Accepts_a_marker_inside_a_raw_json_array_claim()
    {
        // Some IdPs pass the whole amr array through as one claim value.
        var principal = PrincipalWith(new Claim("amr", """["pwd","mfa"]"""));

        Assert.True(MfaEnforcement.SatisfiesMfa(principal, Defaults));
    }

    [Fact]
    public void Rejects_a_password_only_token()
    {
        var principal = PrincipalWith(new Claim("amr", "pwd"));

        Assert.False(MfaEnforcement.SatisfiesMfa(principal, Defaults));
    }

    [Fact]
    public void Rejects_a_token_with_no_amr_claim_at_all()
    {
        var principal = PrincipalWith(new Claim("sub", "u1"));

        Assert.False(MfaEnforcement.SatisfiesMfa(principal, Defaults));
    }

    [Fact]
    public void Rejects_a_json_array_with_only_single_factor_methods()
    {
        var principal = PrincipalWith(new Claim("amr", """["pwd"]"""));

        Assert.False(MfaEnforcement.SatisfiesMfa(principal, Defaults));
    }

    [Fact]
    public void Malformed_json_array_claim_never_counts_as_proof()
    {
        var principal = PrincipalWith(new Claim("amr", "[not-json"));

        Assert.False(MfaEnforcement.SatisfiesMfa(principal, Defaults));
    }

    [Fact]
    public void A_deployment_can_narrow_the_accepted_markers()
    {
        // A passkeys-only deployment: otp no longer counts.
        var options = new AuthOptions { MfaAmrValues = ["fido"] };

        Assert.False(MfaEnforcement.SatisfiesMfa(PrincipalWith(new Claim("amr", "otp")), options));
        Assert.True(MfaEnforcement.SatisfiesMfa(PrincipalWith(new Claim("amr", "fido")), options));
    }
}

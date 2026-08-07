using Plenipo.Application.Authorization;
using Plenipo.Application.Bootstrap;
using Xunit;

namespace Plenipo.Application.Tests;

/// <summary>
/// Auth:Mode validation (ADR 0003): typos fail startup, and the mode combinations that contradict
/// each other are refused before they can produce a deployment that half-works.
/// </summary>
public sealed class AuthModeOptionsTests
{
    private static AuthorizationSourceOptions Database => new() { PermissionSource = "Database" };
    private static AuthorizationSourceOptions Token => new() { PermissionSource = "Token" };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Local")]
    [InlineData("local")]
    [InlineData("Oidc")]
    [InlineData("OIDC")]
    public void Accepts_the_known_modes_in_any_case(string? mode) =>
        new AuthModeOptions { Mode = mode }.ThrowIfInvalid(Database);

    [Fact]
    public void Rejects_a_typo_at_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new AuthModeOptions { Mode = "Lcoal" }.ThrowIfInvalid(Database));
        Assert.Contains("Auth:Mode", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Local_mode_refuses_token_sourced_authorization()
    {
        // The embedded issuer's only role authority IS this database; delegating authorization to an
        // external IdP that doesn't exist is a contradiction, not a configuration.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new AuthModeOptions { Mode = "Local" }.ThrowIfInvalid(Token));
        Assert.Contains("PermissionSource", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_flags_are_mutually_exclusive()
    {
        var local = new AuthModeOptions { Mode = "Local" };
        Assert.True(local.IsLocal);
        Assert.False(local.IsExplicitOidc);

        var unset = new AuthModeOptions();
        Assert.False(unset.IsLocal);
        Assert.False(unset.IsExplicitOidc);
    }
}

/// <summary>The bootstrap additions local mode brings (ADR 0003).</summary>
public sealed class BootstrapLocalAuthOptionsTests
{
    private static readonly IReadOnlyDictionary<string, string[]> NoDeclaredRoles =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    private static BootstrapOptions Valid(string? password = null) => new()
    {
        TenantSlug = "main",
        AdminEmail = "owner@example.test",
        AdminInitialPassword = password,
    };

    [Fact]
    public void Operator_roles_without_a_subject_still_fail_for_external_idps()
    {
        // Unchanged guard: an email-keyed invite binds roles through an unverified claim.
        Assert.Throws<InvalidOperationException>(() => Valid().ThrowIfInvalid(NoDeclaredRoles));
    }

    [Fact]
    public void Operator_roles_without_a_subject_pass_when_the_platform_mints_subjects()
    {
        // Local mode: the platform mints the subject and creates its only credential in the same
        // startup pass — there is no unverified matching step for the guard to protect.
        Valid().ThrowIfInvalid(NoDeclaredRoles, platformIssuesSubjects: true);
    }

    [Fact]
    public void A_too_short_initial_password_fails_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Valid("hunter2").ThrowIfInvalid(NoDeclaredRoles, platformIssuesSubjects: true));
        Assert.Contains("AdminInitialPassword", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_enough_initial_password_passes()
    {
        Valid("a-proper-first-password").ThrowIfInvalid(NoDeclaredRoles, platformIssuesSubjects: true);
    }
}

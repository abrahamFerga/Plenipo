using Plenipo.Application.Auditing;
using Plenipo.Application.Usage;
using Plenipo.Core.Multitenancy;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.LocalAuth;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Plenipo.Infrastructure.Tests.LocalAuth;

/// <summary>
/// The credential lifecycle behind Auth:Mode=Local (ADR 0003): hashing, lockout, temporary
/// passwords, stamp rotation, TOTP enrollment — each with its audit trail.
/// </summary>
public sealed class LocalCredentialServiceTests : IDisposable
{
    private readonly PlatformDbContext _db;
    private readonly RecordingAuditLog _audit = new();
    private readonly LocalCredentialService _service;
    private readonly Guid _tenantId = Guid.CreateVersion7();

    public LocalCredentialServiceTests()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"local-credentials-{Guid.NewGuid():N}")
            .Options;
        _db = new PlatformDbContext(options, new TestTenantContext { TenantId = _tenantId });
        _service = new LocalCredentialService(_db, new EphemeralDataProtectionProvider(), _audit);
    }

    public void Dispose() => _db.Dispose();

    private async Task<User> AddUserAsync(string email, Guid? tenantId = null)
    {
        var user = new User
        {
            TenantId = tenantId ?? _tenantId,
            Subject = $"local|{Guid.CreateVersion7():N}",
            Email = email,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Creates_with_a_generated_temporary_password_that_verifies_and_forces_change()
    {
        var user = await AddUserAsync("ada@example.test");

        var (credential, password, error) = await _service.CreateAsync(user, null, "127.0.0.1", default);

        Assert.Null(error);
        Assert.NotNull(credential);
        Assert.NotNull(password);
        // xxxx-xxxx-xxxx-xxxx from the unambiguous alphabet — readable from a log, typed once.
        Assert.Matches("^[a-z2-9]{4}-[a-z2-9]{4}-[a-z2-9]{4}-[a-z2-9]{4}$", password);
        Assert.DoesNotContain(password, chars => "01loi".Contains(chars));
        Assert.True(credential!.MustChangePassword);
        Assert.True(await _service.VerifyPasswordAsync(credential, password!, default));
        Assert.False(await _service.VerifyPasswordAsync(credential, "not-the-password", default));
        Assert.Contains(_audit.AuthEvents, e => e.EventType == AuthAuditEventType.LocalCredentialCreated);
    }

    [Fact]
    public async Task Rejects_a_duplicate_email_even_in_another_tenant()
    {
        var first = await AddUserAsync("shared@example.test");
        await _service.CreateAsync(first, null, null, default);

        var otherTenant = await AddUserAsync("SHARED@example.test", Guid.CreateVersion7());
        var (credential, _, error) = await _service.CreateAsync(otherTenant, null, null, default);

        // Deployment-wide by design: the anonymous login form has no tenant field, so an email must
        // name exactly one credential on this host.
        Assert.Null(credential);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Five_failures_lock_the_credential_and_unlock_clears_it()
    {
        var user = await AddUserAsync("locked@example.test");
        var (credential, _, _) = await _service.CreateAsync(user, null, null, default);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await _service.RegisterFailureAsync(credential!, "wrong password", null, default);
            Assert.False(LocalCredentialService.IsLockedOut(credential!));
        }

        await _service.RegisterFailureAsync(credential!, "wrong password", null, default);
        Assert.True(LocalCredentialService.IsLockedOut(credential!));
        Assert.Contains(_audit.AuthEvents, e => e.EventType == AuthAuditEventType.LocalLockedOut);

        await _service.UnlockAsync(credential!, null, default);
        Assert.False(LocalCredentialService.IsLockedOut(credential!));
        Assert.Equal(0, credential!.FailedLoginCount);
    }

    [Fact]
    public async Task Changing_the_password_rotates_the_stamp_and_ends_the_forced_change()
    {
        var user = await AddUserAsync("rotate@example.test");
        var (credential, _, _) = await _service.CreateAsync(user, null, null, default);
        var originalStamp = credential!.SecurityStamp;

        var error = await _service.SetPasswordAsync(
            credential, "a-long-enough-password", mustChange: false, byAdminReset: false, null, default);

        Assert.Null(error);
        Assert.NotEqual(originalStamp, credential.SecurityStamp);
        Assert.False(credential.MustChangePassword);
        Assert.True(await _service.VerifyPasswordAsync(credential, "a-long-enough-password", default));
        Assert.Contains(_audit.AuthEvents, e => e.EventType == AuthAuditEventType.LocalPasswordChanged);
    }

    [Fact]
    public async Task An_admin_reset_issues_a_fresh_temporary_password_and_rotates_the_stamp()
    {
        var user = await AddUserAsync("reset@example.test");
        var (credential, original, _) = await _service.CreateAsync(user, null, null, default);
        var originalStamp = credential!.SecurityStamp;

        var temporary = await _service.ResetToTemporaryAsync(credential, null, default);

        Assert.NotEqual(original, temporary);
        Assert.NotEqual(originalStamp, credential.SecurityStamp);
        Assert.True(credential.MustChangePassword);
        Assert.True(await _service.VerifyPasswordAsync(credential, temporary, default));
        Assert.Contains(_audit.AuthEvents, e => e.EventType == AuthAuditEventType.LocalCredentialReset);
    }

    [Theory]
    [InlineData(null, "A password is required.")]
    [InlineData("", "A password is required.")]
    [InlineData("elevenchars", "Use at least 12 characters.")]
    public void Password_policy_is_length_only(string? password, string expected) =>
        Assert.Equal(expected, LocalCredentialService.ValidatePassword(password));

    [Fact]
    public void Twelve_characters_pass_the_policy() =>
        Assert.Null(LocalCredentialService.ValidatePassword("twelve-chars"));

    [Fact]
    public async Task Totp_enrollment_only_activates_on_a_confirmed_code()
    {
        var user = await AddUserAsync("totp@example.test");
        var (credential, _, _) = await _service.CreateAsync(user, null, null, default);

        var secret = await _service.StartTotpEnrollmentAsync(credential!, default);
        Assert.Null(credential!.TotpEnabledAt); // pending until a code proves the app has the secret

        Assert.False(await _service.ConfirmTotpEnrollmentAsync(credential, "000000", null, default));
        Assert.Null(credential.TotpEnabledAt);

        var code = Totp.ComputeCode(Totp.FromBase32(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        Assert.True(await _service.ConfirmTotpEnrollmentAsync(credential, code, null, default));
        Assert.NotNull(credential.TotpEnabledAt);
        Assert.True(_service.VerifyTotp(credential, code));
        Assert.Contains(_audit.AuthEvents, e => e.EventType == AuthAuditEventType.LocalMfaEnrolled);

        await _service.DisableTotpAsync(credential, "test", null, default);
        Assert.Null(credential.TotpSecret);
        Assert.Null(credential.TotpEnabledAt);
        Assert.Contains(_audit.AuthEvents, e => e.EventType == AuthAuditEventType.LocalMfaDisabled);
    }

    [Fact]
    public async Task A_successful_sign_in_resets_the_failure_budget_and_audits()
    {
        var user = await AddUserAsync("signin@example.test");
        var (credential, _, _) = await _service.CreateAsync(user, null, null, default);
        await _service.RegisterFailureAsync(credential!, "wrong password", null, default);

        await _service.RegisterSignInAsync(credential!, user, usedTotp: false, "10.0.0.9", default);

        Assert.Equal(0, credential!.FailedLoginCount);
        Assert.NotNull(credential.LastSignInAt);
        Assert.Contains(_audit.AuthEvents,
            e => e.EventType == AuthAuditEventType.SignIn && e.Detail == "local password");
    }

    private sealed class TestTenantContext : ITenantContext
    {
        public Guid? TenantId { get; set; }
        public bool HasTenant => TenantId is not null;
        public Guid RequireTenantId() => TenantId ?? throw new InvalidOperationException("No tenant.");
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuthAuditEntry> AuthEvents { get; } = [];

        public Task RecordToolCallAsync(ToolCallAuditEntry entry, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordAuthEventAsync(AuthAuditEntry entry, CancellationToken cancellationToken = default)
        {
            AuthEvents.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordEntityChangesAsync(IReadOnlyCollection<EntityChangeAuditEntry> entries, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordTokenUsageAsync(TokenUsageRecord record, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

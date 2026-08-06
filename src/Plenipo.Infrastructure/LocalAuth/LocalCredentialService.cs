using System.Security.Cryptography;
using Plenipo.Application.Auditing;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.LocalAuth;

/// <summary>
/// Every read and write of a <see cref="LocalCredential"/> (ADR 0003): password hashing and
/// verification, lockout, temporary passwords, TOTP enrollment, and the audit trail for all of it.
/// Sign-in-path lookups run <c>IgnoreQueryFilters</c> on purpose — the login page is anonymous, so
/// there is no ambient tenant yet; the credential row itself says which tenant the user belongs to.
/// </summary>
public sealed class LocalCredentialService(
    PlatformDbContext db,
    IDataProtectionProvider dataProtection,
    IAuditLog auditLog)
{
    /// <summary>NIST-style: length is the policy. No composition rules to work around.</summary>
    public const int MinPasswordLength = 12;

    public const int MaxPasswordLength = 256;

    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>Unambiguous lowercase alphabet (no 0/o, 1/l/i) — read from a log, typed once.</summary>
    private const string TempPasswordAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private const string TotpProtectorPurpose = "Plenipo.LocalAuth.Totp";

    // Stateless and thread-safe; the type parameter only namespaces the hash format.
    private static readonly PasswordHasher<User> Hasher = new();

    /// <summary>An error sentence when the password is not acceptable, otherwise null.</summary>
    public static string? ValidatePassword(string? password) => password switch
    {
        null or "" => "A password is required.",
        { Length: < MinPasswordLength } => $"Use at least {MinPasswordLength} characters.",
        { Length: > MaxPasswordLength } => $"Use at most {MaxPasswordLength} characters.",
        _ => null,
    };

    /// <summary>A generated temporary password (~77 bits): xxxx-xxxx-xxxx-xxxx, unambiguous characters.</summary>
    public static string GenerateTemporaryPassword()
    {
        Span<char> chars = stackalloc char[19];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (i + 1) % 5 == 0
                ? '-'
                : TempPasswordAlphabet[RandomNumberGenerator.GetInt32(TempPasswordAlphabet.Length)];
        }

        return new string(chars);
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Sign-in lookup: anonymous context, so no ambient tenant — filters ignored by design.</summary>
    public Task<LocalCredential?> FindForSignInAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email);
        return db.LocalCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Email == normalized, cancellationToken);
    }

    /// <summary>Admin lookup: runs under the caller's tenant filter, so cross-tenant rows stay invisible.</summary>
    public Task<LocalCredential?> FindForUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.LocalCredentials.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);

    /// <summary>
    /// Creates the credential for a user. <paramref name="password"/> null generates a temporary one;
    /// either way the first sign-in must change it. Returns the plaintext exactly once — it is never
    /// reconstructable afterwards.
    /// </summary>
    public async Task<(LocalCredential? Credential, string? Password, string? Error)> CreateAsync(
        User user, string? password, string? ipAddress, CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(user.Email);
        if (!email.Contains('@', StringComparison.Ordinal))
        {
            return (null, null, $"'{user.Email}' is not an email address a person can sign in with.");
        }

        // Deployment-wide, not per tenant: the login form has no tenant field, so an email must name
        // exactly one credential across every tenant on this host (ADR 0003). IgnoreQueryFilters is
        // what makes the check deployment-wide; the unique index arbitrates any write race.
        if (await db.LocalCredentials.IgnoreQueryFilters()
                .AnyAsync(c => c.Email == email || c.UserId == user.Id, cancellationToken))
        {
            return (null, null, $"{email} already has local sign-in on this deployment (possibly in another tenant).");
        }

        if (password is not null && ValidatePassword(password) is { } error)
        {
            return (null, null, error);
        }

        var initialPassword = password ?? GenerateTemporaryPassword();
        var credential = new LocalCredential
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            Email = email,
            PasswordHash = Hasher.HashPassword(user, initialPassword),
            SecurityStamp = NewStamp(),
            MustChangePassword = true,
        };
        db.LocalCredentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            Subject = user.Subject,
            UserDisplay = user.DisplayName,
            EventType = AuthAuditEventType.LocalCredentialCreated,
            Detail = password is null ? "temporary password generated" : "initial password supplied",
            IpAddress = ipAddress,
        }, cancellationToken);

        return (credential, initialPassword, null);
    }

    /// <summary>True when the password matches (transparently upgrading the hash if its format aged).</summary>
    public async Task<bool> VerifyPasswordAsync(
        LocalCredential credential, string password, CancellationToken cancellationToken)
    {
        var result = Hasher.VerifyHashedPassword(null!, credential.PasswordHash, password);
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            credential.PasswordHash = Hasher.HashPassword(null!, password);
            await db.SaveChangesAsync(cancellationToken);
        }

        return result is not PasswordVerificationResult.Failed;
    }

    public static bool IsLockedOut(LocalCredential credential) =>
        credential.LockedUntil is { } until && until > DateTimeOffset.UtcNow;

    /// <summary>Counts a failed attempt and locks the credential when the budget is spent.</summary>
    public async Task RegisterFailureAsync(
        LocalCredential credential, string detail, string? ipAddress, CancellationToken cancellationToken)
    {
        credential.FailedLoginCount++;
        var lockedNow = credential.FailedLoginCount >= MaxFailedAttempts;
        if (lockedNow)
        {
            credential.LockedUntil = DateTimeOffset.UtcNow + LockoutDuration;
            credential.FailedLoginCount = 0;
        }

        await db.SaveChangesAsync(cancellationToken);
        await auditLog.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = credential.TenantId,
            UserId = credential.UserId,
            Subject = credential.Email,
            EventType = lockedNow ? AuthAuditEventType.LocalLockedOut : AuthAuditEventType.LocalSignInFailed,
            Detail = lockedNow ? $"{detail}; locked for {LockoutDuration.TotalMinutes:0} minutes" : detail,
            IpAddress = ipAddress,
        }, cancellationToken);
    }

    /// <summary>Resets the failure budget and stamps the successful sign-in into the audit trail.</summary>
    public async Task RegisterSignInAsync(
        LocalCredential credential, User user, bool usedTotp, string? ipAddress, CancellationToken cancellationToken)
    {
        credential.FailedLoginCount = 0;
        credential.LockedUntil = null;
        credential.LastSignInAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = credential.TenantId,
            UserId = credential.UserId,
            Subject = user.Subject,
            UserDisplay = user.DisplayName,
            EventType = AuthAuditEventType.SignIn,
            Detail = usedTotp ? "local password + totp" : "local password",
            IpAddress = ipAddress,
        }, cancellationToken);
    }

    /// <summary>
    /// Sets a new password and rotates the security stamp — which is what ends every session and
    /// refresh token the old password minted.
    /// </summary>
    public async Task<string?> SetPasswordAsync(
        LocalCredential credential, string newPassword, bool mustChange, bool byAdminReset,
        string? ipAddress, CancellationToken cancellationToken)
    {
        if (ValidatePassword(newPassword) is { } error)
        {
            return error;
        }

        credential.PasswordHash = Hasher.HashPassword(null!, newPassword);
        credential.SecurityStamp = NewStamp();
        credential.MustChangePassword = mustChange;
        credential.FailedLoginCount = 0;
        credential.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = credential.TenantId,
            UserId = credential.UserId,
            Subject = credential.Email,
            EventType = byAdminReset ? AuthAuditEventType.LocalCredentialReset : AuthAuditEventType.LocalPasswordChanged,
            IpAddress = ipAddress,
        }, cancellationToken);
        return null;
    }

    /// <summary>Admin reset: new temporary password (returned once), change forced at next sign-in.</summary>
    public async Task<string> ResetToTemporaryAsync(
        LocalCredential credential, string? ipAddress, CancellationToken cancellationToken)
    {
        var password = GenerateTemporaryPassword();
        await SetPasswordAsync(credential, password, mustChange: true, byAdminReset: true, ipAddress, cancellationToken);
        return password;
    }

    public async Task UnlockAsync(LocalCredential credential, string? ipAddress, CancellationToken cancellationToken)
    {
        credential.LockedUntil = null;
        credential.FailedLoginCount = 0;
        await db.SaveChangesAsync(cancellationToken);
        await auditLog.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = credential.TenantId,
            UserId = credential.UserId,
            Subject = credential.Email,
            EventType = AuthAuditEventType.LocalCredentialReset,
            Detail = "unlocked by an admin (password unchanged)",
            IpAddress = ipAddress,
        }, cancellationToken);
    }

    /// <summary>
    /// Starts (or restarts) TOTP enrollment: a fresh secret, stored protected but NOT yet enabled —
    /// only a confirmed code activates it, so a user can never lock themselves out with an app that
    /// never scanned the secret. Returns the base32 secret for the authenticator app.
    /// </summary>
    public async Task<string> StartTotpEnrollmentAsync(LocalCredential credential, CancellationToken cancellationToken)
    {
        var secret = Totp.GenerateSecret();
        credential.TotpSecret = dataProtection.CreateProtector(TotpProtectorPurpose).Protect(secret);
        credential.TotpEnabledAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return secret;
    }

    /// <summary>Activates a pending enrollment when the code proves the authenticator holds the secret.</summary>
    public async Task<bool> ConfirmTotpEnrollmentAsync(
        LocalCredential credential, string code, string? ipAddress, CancellationToken cancellationToken)
    {
        if (credential.TotpSecret is null || !VerifyTotp(credential, code))
        {
            return false;
        }

        credential.TotpEnabledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await auditLog.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = credential.TenantId,
            UserId = credential.UserId,
            Subject = credential.Email,
            EventType = AuthAuditEventType.LocalMfaEnrolled,
            IpAddress = ipAddress,
        }, cancellationToken);
        return true;
    }

    public bool VerifyTotp(LocalCredential credential, string code) =>
        credential.TotpSecret is { } protectedSecret
        && Totp.Verify(
            dataProtection.CreateProtector(TotpProtectorPurpose).Unprotect(protectedSecret),
            code,
            DateTimeOffset.UtcNow);

    public async Task DisableTotpAsync(
        LocalCredential credential, string detail, string? ipAddress, CancellationToken cancellationToken)
    {
        credential.TotpSecret = null;
        credential.TotpEnabledAt = null;
        await db.SaveChangesAsync(cancellationToken);
        await auditLog.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = credential.TenantId,
            UserId = credential.UserId,
            Subject = credential.Email,
            EventType = AuthAuditEventType.LocalMfaDisabled,
            Detail = detail,
            IpAddress = ipAddress,
        }, cancellationToken);
    }

    private static string NewStamp() => Guid.CreateVersion7().ToString("N");
}

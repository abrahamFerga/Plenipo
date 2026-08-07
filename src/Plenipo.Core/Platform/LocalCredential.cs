using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

/// <summary>
/// Password sign-in state for one <see cref="User"/> when the deployment runs <c>Auth:Mode=Local</c>
/// (ADR 0003) — the only place the platform ever stores a credential. Rows exist solely for users an
/// admin (or bootstrap) explicitly created a password for; in external-IdP deployments the table
/// stays empty and nothing reads it.
/// </summary>
public sealed class LocalCredential : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>The platform user this credential signs in. One credential per user.</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Sign-in email, normalized to lowercase. Unique across the whole deployment — not per tenant —
    /// so the login form never needs a tenant field; the user's tenant travels in the issued token.
    /// </summary>
    public required string Email { get; set; }

    /// <summary>PBKDF2 hash in ASP.NET Core Identity's self-describing format.</summary>
    public required string PasswordHash { get; set; }

    /// <summary>
    /// Rotated on every password change or admin reset. Embedded in issued tokens and compared on
    /// refresh, so rotating it ends every outstanding session the old password minted.
    /// </summary>
    public required string SecurityStamp { get; set; }

    /// <summary>Set on admin-issued temporary passwords; the login flow forces a change before sign-in.</summary>
    public bool MustChangePassword { get; set; }

    public int FailedLoginCount { get; set; }

    /// <summary>Lockout horizon after repeated failures; null / past means not locked.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>RFC 6238 TOTP secret (base32), protected at rest with Data Protection. Null until enrolled.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>When TOTP was confirmed with a valid code; null means MFA is not enabled.</summary>
    public DateTimeOffset? TotpEnabledAt { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }
}

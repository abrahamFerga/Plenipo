namespace Plenipo.Application.Auditing;

public enum AuthAuditEventType
{
    SignIn = 0,
    SignOut = 1,
    UserProvisioned = 2,
    PermissionGranted = 3,
    PermissionRevoked = 4,
    AccessDenied = 5,

    /// <summary>A role's permission baseline was reconfigured (Layer 1 → Layer 2 mapping changed).</summary>
    RolePermissionsChanged = 6,

    /// <summary>A role was assigned to a user.</summary>
    RoleAssigned = 7,

    /// <summary>A role was revoked from a user.</summary>
    RoleRevoked = 8,

    /// <summary>A custom (tenant-defined) role was created.</summary>
    RoleCreated = 9,

    /// <summary>A custom (tenant-defined) role was deleted.</summary>
    RoleDeleted = 10,

    /// <summary>A standing email invite was redeemed at a user's first sign-in.</summary>
    InviteRedeemed = 12,

    /// <summary>A new user was refused because the tenant's subscription seat limit is reached.</summary>
    SeatLimitDenied = 11,

    /// <summary>An inspect-only agent security control detected a risk without blocking the turn.</summary>
    AgentSecurityDetected = 13,

    /// <summary>An enforcing agent security control blocked content before it crossed a trust boundary.</summary>
    AgentSecurityBlocked = 14,

    /// <summary>A configured external agent security detector could not make a decision.</summary>
    AgentSecurityUnavailable = 15,

    /// <summary>
    /// The deployment's first tenant and operator were created from the <c>Bootstrap</c> configuration
    /// section. Happens at most once per deployment. The NAME is what persists (the column is a string),
    /// so it must never be renamed.
    /// </summary>
    PlatformBootstrapped = 16,

    // Local auth mode (Auth:Mode=Local, ADR 0003) — the embedded issuer's credential lifecycle.
    // Successful local sign-ins record the existing SignIn event; these cover everything around it.

    /// <summary>A local sign-in attempt failed (wrong password or TOTP code).</summary>
    LocalSignInFailed = 17,

    /// <summary>Repeated failures locked a local credential until its lockout horizon.</summary>
    LocalLockedOut = 18,

    /// <summary>A user changed their own local password (rotates the security stamp).</summary>
    LocalPasswordChanged = 19,

    /// <summary>An admin (or bootstrap) created a local credential with a temporary password.</summary>
    LocalCredentialCreated = 20,

    /// <summary>An admin reset a local credential to a new temporary password (rotates the stamp).</summary>
    LocalCredentialReset = 21,

    /// <summary>The user confirmed a TOTP enrollment with a valid code.</summary>
    LocalMfaEnrolled = 22,

    /// <summary>TOTP was removed from a local credential (by the user or an admin reset).</summary>
    LocalMfaDisabled = 23,
}

/// <summary>Append-only record of an identity / authorization event.</summary>
public sealed class AuthAuditEntry
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid? TenantId { get; init; }
    public Guid? UserId { get; init; }
    public string? Subject { get; init; }
    public string? UserDisplay { get; init; }

    public required AuthAuditEventType EventType { get; init; }

    /// <summary>Free-form detail, e.g. the permission affected or the endpoint that denied access.</summary>
    public string? Detail { get; init; }

    public string? IpAddress { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

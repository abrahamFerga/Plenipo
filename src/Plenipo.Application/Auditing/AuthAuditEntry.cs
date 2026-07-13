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

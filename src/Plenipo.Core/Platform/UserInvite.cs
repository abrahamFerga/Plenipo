using Plenipo.Core.Entities;

namespace Plenipo.Core.Platform;

/// <summary>
/// A standing invitation: an admin names an email address and the roles it should start with,
/// BEFORE that person has ever signed in. When someone authenticates with a matching email
/// (any IdP — the invite is keyed on the address, not a token link), the just-in-time
/// provisioning in the request enricher applies the invited roles and marks the invite redeemed.
/// Until then the admin can revoke it. This closes the gap where roles could only be assigned
/// to users who already had a row.
/// </summary>
public sealed class UserInvite : TenantEntityBase
{
    /// <summary>Invited address, normalized lowercase — matched against the sign-in email claim.</summary>
    public required string Email { get; set; }

    /// <summary>Comma-separated role names applied at first sign-in (empty = the default role).</summary>
    public string Roles { get; set; } = "";

    public DateTimeOffset? RedeemedAt { get; set; }

    /// <summary>The user the invite became, once redeemed.</summary>
    public Guid? RedeemedByUserId { get; set; }

    public string[] RoleList() =>
        Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

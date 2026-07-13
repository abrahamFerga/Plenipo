using Plenipo.Core.Entities;

namespace Plenipo.Modules.Legal.Persistence;

public enum PartyRole
{
    /// <summary>The firm's own client on this matter.</summary>
    Client = 0,

    /// <summary>The party on the other side — the one a future engagement must not be adverse to.</summary>
    Adverse = 1,

    /// <summary>Anyone else material to conflicts (co-counsel, witnesses, affiliates, insurers).</summary>
    Related = 2,
}

/// <summary>
/// A party recorded on a matter — the raw material of the conflict-of-interest check every firm
/// must run before opening an engagement. Parties are recorded at intake (and as they emerge) so
/// <c>check_conflicts</c> can search across ALL of the tenant's matters, including walled ones
/// (which report as restricted hits without leaking their names).
/// </summary>
public sealed class MatterParty : TenantEntityBase
{
    public Guid MatterId { get; set; }

    public required string Name { get; set; }

    public PartyRole Role { get; set; } = PartyRole.Client;

    /// <summary>Optional context (e.g. "parent company of client", "opposing counsel: X LLP").</summary>
    public string? Notes { get; set; }
}

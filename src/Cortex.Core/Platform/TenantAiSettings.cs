using Cortex.Core.Entities;
using Cortex.Core.Multitenancy;

namespace Cortex.Core.Platform;

/// <summary>
/// Per-tenant overrides for the AI assistant. Each field is optional — <c>null</c> means "use the
/// deployment default" (from the <c>Ai</c> configuration). Lets one tenant run a bespoke system prompt or a
/// tighter conversation token budget without a code change or affecting other tenants. One row per tenant.
/// </summary>
public sealed class TenantAiSettings : EntityBase, ITenantOwned
{
    public Guid TenantId { get; set; }

    /// <summary>Overrides the base system prompt for this tenant's agents when set.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>Overrides the max tokens a conversation may consume before further turns are refused. Null = default.</summary>
    public int? MaxConversationTokens { get; set; }

    /// <summary>Overrides the tenant's monthly token budget (UTC calendar month). Null = default; 0 = unlimited.</summary>
    public long? MaxMonthlyTokens { get; set; }
}

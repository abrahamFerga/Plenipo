using Plenipo.Core.Entities;
using Plenipo.Core.Multitenancy;

namespace Plenipo.Core.Platform;

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

    /// <summary>
    /// Overrides the AI provider for this tenant (Mock | OpenAI | AzureOpenAI | Ollama | None).
    /// Null = the deployment default. When set, <see cref="Model"/>, <see cref="Endpoint"/> and the
    /// vaulted API key describe the tenant's OWN provider connection (SaaS bring-your-own-key).
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>Overrides the model/deployment name. Null = the deployment default model.</summary>
    public string? Model { get; set; }

    /// <summary>Provider endpoint (Azure OpenAI resource / Ollama base URL) when the provider is overridden.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Opaque <c>ISecretVault</c> reference to the tenant's API key. The key itself never lands in
    /// this table and is never returned by any API — admins can only replace or clear it.
    /// </summary>
    public string? ApiKeySecretRef { get; set; }
}

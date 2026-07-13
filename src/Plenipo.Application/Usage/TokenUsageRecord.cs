namespace Plenipo.Application.Usage;

/// <summary>
/// Append-only record of the token consumption of a single agent turn. Written to the audit store
/// alongside tool-call and auth records. Aggregated by the admin dashboard for per-tenant / per-user /
/// per-module cost monitoring. Mirrors the MAF token-usage example: capture <c>response.Usage</c> and
/// persist it for observability.
/// </summary>
public sealed class TokenUsageRecord
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; init; }
    public Guid? UserId { get; init; }
    public string? UserDisplay { get; init; }

    public required string ModuleId { get; init; }
    public Guid? ConversationId { get; init; }

    /// <summary>The provider that served the turn (OpenAI / AzureOpenAI / Ollama).</summary>
    public required string Provider { get; init; }

    /// <summary>The model / deployment name that served the turn.</summary>
    public required string Model { get; init; }

    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long TotalTokens { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

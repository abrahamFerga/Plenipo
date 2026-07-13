namespace Plenipo.Application.Auditing;

/// <summary>
/// Append-only record of a single agent tool invocation — the spine of the platform's "audit
/// everything the agent does" guarantee. Written to the separate audit store, never updated.
/// </summary>
public sealed class ToolCallAuditEntry
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; init; }
    public Guid? UserId { get; init; }
    public string? UserDisplay { get; init; }

    public required string ModuleId { get; init; }
    public required string ToolName { get; init; }
    public required string Permission { get; init; }

    /// <summary>Serialized tool arguments. Treat as potentially sensitive when querying.</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>Serialized tool result (omitted or truncated for large / sensitive payloads).</summary>
    public string? ResultJson { get; init; }

    public bool Success { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }

    public Guid? ConversationId { get; init; }
    public string? CorrelationId { get; init; }
    public string? IpAddress { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

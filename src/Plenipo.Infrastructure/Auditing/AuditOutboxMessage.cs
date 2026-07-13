namespace Plenipo.Infrastructure.Auditing;

/// <summary>The audit-record kinds carried by an <see cref="AuditOutboxMessage"/>.</summary>
public static class AuditRecordKind
{
    public const string ToolCall = "ToolCall";
    public const string AuthEvent = "AuthEvent";
    public const string EntityChange = "EntityChange";
    public const string TokenUsage = "TokenUsage";
}

/// <summary>
/// A durable, serialized audit record parked in the platform database when the direct write to the audit
/// store fails (e.g. a transient audit-DB outage). The <c>AuditOutboxProcessor</c> drains these into the
/// audit store on a schedule, so a momentary outage defers audit records rather than dropping them — the
/// platform's "audit everything" guarantee survives an audit-DB blip.
/// </summary>
public sealed class AuditOutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>One of <see cref="AuditRecordKind"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The JSON-serialized audit entry (or array of entries, for an entity-change batch).</summary>
    public required string PayloadJson { get; init; }

    /// <summary>How many times the processor has tried (and failed) to flush this message — for diagnostics.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

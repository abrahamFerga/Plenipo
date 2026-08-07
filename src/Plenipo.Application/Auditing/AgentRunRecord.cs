namespace Plenipo.Application.Auditing;

/// <summary>
/// Append-only record of one agent turn — the join key that turns the platform's existing tool-call,
/// auth and token-usage rows into a reconstructable conversation.
///
/// <para>Exactly one of these is written per turn, on every exit path: completed, refused, blocked,
/// over budget, thrown, or abandoned. That guarantee is what <c>TokenUsageRecord</c> cannot give —
/// usage is only reported when a provider actually billed something, so the failures operators most
/// need to see are exactly the ones it omits.</para>
///
/// <para><b>Relationship to the other stores.</b> The OpenTelemetry span for the same turn carries
/// the live detail and expires with the trace-retention window; <see cref="TraceId"/> is the join
/// back to it while it lasts. This row is the durable, tenant-scoped, queryable evidence.</para>
/// </summary>
public sealed class AgentRunRecord
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid TenantId { get; init; }
    public Guid? UserId { get; init; }
    public string? UserDisplay { get; init; }

    public required string ModuleId { get; init; }

    /// <summary>Null until the conversation is resolved — a turn can fail before one exists.</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>The agent (tenant profile or manifest agent) the caller picked, if any.</summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Set on the parent run of a workflow. Its steps are separate runs carrying
    /// <see cref="ParentRunId"/>, so the explorer can nest a chain under the turn that started it.
    /// </summary>
    public string? WorkflowName { get; set; }

    /// <summary>The workflow run this turn is a step of; null for an ordinary turn.</summary>
    public Guid? ParentRunId { get; init; }

    /// <summary>The provider that served the turn, or null when the turn failed before one was resolved.</summary>
    public string? Provider { get; set; }

    /// <summary>The effective model/deployment — the profile's pin or per-turn override, not the tenant default.</summary>
    public string? Model { get; set; }

    /// <summary>Hash of the exact instruction assembly the turn ran under; resolves via <c>InstructionSnapshot</c>.</summary>
    public string? InstructionsHash { get; set; }

    public AgentRunOutcome Outcome { get; set; } = AgentRunOutcome.Cancelled;

    /// <summary>
    /// The exception type (or a short internal code such as <c>UnknownAgent</c>). Deliberately the
    /// INTERNAL classification — the user-facing text is sanitized and is useless for diagnosis.
    /// </summary>
    public string? ErrorKind { get; set; }

    /// <summary>The internal error detail, capped to its column. Null on a successful turn.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Milliseconds to the first streamed token; null when the turn produced none.</summary>
    public long? FirstTokenMs { get; set; }

    /// <summary>Wall-clock milliseconds for the whole turn, including a failure that ended it early.</summary>
    public long TotalMs { get; set; }

    /// <summary>Distinct tools the model invoked during the turn.</summary>
    public int ToolCallCount { get; set; }

    /// <summary>Tools the turn blocked pending human approval.</summary>
    public int ApprovalCount { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }

    /// <summary>Cached (discounted) input tokens the provider reported. Populated with the price book.</summary>
    public long CachedInputTokens { get; set; }

    /// <summary>Reasoning tokens the provider reported, where it separates them from output.</summary>
    public long ReasoningTokens { get; set; }

    /// <summary>
    /// Cost stamped at write time from the price book in force for this turn — never recomputed on
    /// read, so a later price change cannot rewrite history. Null when no rate was configured.
    /// </summary>
    public decimal? Cost { get; set; }

    /// <summary>ISO currency of <see cref="Cost"/>.</summary>
    public string? Currency { get; set; }

    /// <summary>The OpenTelemetry trace this turn ran under, while that trace is still retained.</summary>
    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

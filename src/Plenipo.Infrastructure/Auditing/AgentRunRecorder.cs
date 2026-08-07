using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Plenipo.Application.Agents;
using Plenipo.Application.Auditing;
using Plenipo.Core.Identity;

namespace Plenipo.Infrastructure.Auditing;

/// <summary>
/// Accumulates one turn's audit facts while it runs, then writes a single <see cref="AgentRunRecord"/>.
///
/// <para><b>First outcome wins.</b> Once a turn has been decided — blocked, refused, over budget,
/// thrown — nothing later overwrites that verdict. This matters for the output-guardrail case, where
/// the turn is blocked by policy but still persists a (rewritten) reply and would otherwise go on to
/// report itself as <see cref="AgentRunOutcome.Completed"/>.</para>
///
/// <para>The record starts out as <see cref="AgentRunOutcome.Cancelled"/> so that a turn nobody ever
/// decided — a client that hung up mid-stream — is recorded honestly rather than as a success.</para>
/// </summary>
public sealed class AgentRunRecorder
{
    private const int MaxErrorMessage = 2000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly AgentRunRecord _record;
    private bool _decided;

    public AgentRunRecorder(ICurrentUser user, AgentRunRequest request, Guid? parentRunId)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        // Captured at construction: the ambient span here is the turn's own, before any tool or
        // provider call replaces Activity.Current with a child.
        var activity = Activity.Current;

        _record = new AgentRunRecord
        {
            TenantId = user.TenantId ?? Guid.Empty,
            UserId = user.UserId,
            UserDisplay = user.DisplayName,
            ModuleId = request.ModuleId,
            ConversationId = request.ConversationId,
            AgentName = string.IsNullOrWhiteSpace(request.Agent) ? null : request.Agent,
            ParentRunId = parentRunId,
            TraceId = activity?.TraceId.ToString(),
            SpanId = activity?.SpanId.ToString(),
        };
    }

    /// <summary>This run's id — the parent id for the steps of a workflow started by this turn.</summary>
    public Guid Id => _record.Id;

    public void Conversation(Guid conversationId) => _record.ConversationId = conversationId;

    public void Workflow(string workflowName) => _record.WorkflowName = workflowName;

    public void Provider(string provider, string model)
    {
        _record.Provider = provider;
        _record.Model = model;
    }

    public void Instructions(string instructionsHash) => _record.InstructionsHash = instructionsHash;

    /// <summary>Stamps time-to-first-token the first time the turn emits text; later tokens are ignored.</summary>
    public void FirstToken() => _record.FirstTokenMs ??= _clock.ElapsedMilliseconds;

    public void Tools(int toolCallCount, int approvalCount)
    {
        _record.ToolCallCount = toolCallCount;
        _record.ApprovalCount = approvalCount;
    }

    public void Usage(long inputTokens, long outputTokens, long totalTokens)
    {
        _record.InputTokens = inputTokens;
        _record.OutputTokens = outputTokens;
        _record.TotalTokens = totalTokens;
    }

    public void Complete() => Decide(AgentRunOutcome.Completed, null, null);

    /// <summary>
    /// Records why the turn did not succeed. <paramref name="kind"/> is the INTERNAL classification
    /// (an exception type, or a short code such as <c>UnknownAgent</c>) — never the sanitized text
    /// shown to the user, which by design says nothing an operator could act on.
    /// </summary>
    public void Fail(AgentRunOutcome outcome, string kind, string? message = null) =>
        Decide(outcome, kind, message);

    private void Decide(AgentRunOutcome outcome, string? kind, string? message)
    {
        if (_decided)
        {
            return;
        }

        _decided = true;
        _record.Outcome = outcome;
        _record.ErrorKind = kind;
        _record.ErrorMessage = message is { Length: > MaxErrorMessage } ? message[..MaxErrorMessage] : message;
    }

    /// <summary>
    /// Writes the run. Called from the runner's <c>finally</c>, so it deliberately ignores the turn's
    /// cancellation token: a cancelled or abandoned turn is exactly the one worth recording, and
    /// passing the cancelled token would throw before anything reached the audit store. Best-effort
    /// like every other audit write — a failure here never surfaces to the caller.
    /// </summary>
    public async Task FlushAsync(IAuditLog auditLog, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(logger);

        _record.TotalMs = _clock.ElapsedMilliseconds;

        try
        {
            await auditLog.RecordAgentRunAsync(_record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the agent run for conversation {ConversationId}.", _record.ConversationId);
        }
    }
}

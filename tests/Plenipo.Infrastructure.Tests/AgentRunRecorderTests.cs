using Microsoft.Extensions.Logging.Abstractions;
using Plenipo.Application.Agents;
using Plenipo.Application.Auditing;
using Plenipo.Application.Usage;
using Plenipo.Core.Identity;
using Plenipo.Infrastructure.Auditing;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// The recorder's contract, tested away from the runner: one record per turn, the first decisive
/// outcome wins, an undecided turn is honest about it, and a failing audit store never propagates
/// into the turn.
/// </summary>
public sealed class AgentRunRecorderTests
{
    private static AgentRunRecorder Recorder(Guid? parentRunId = null) =>
        new(new FakeCurrentUser(), new AgentRunRequest { ModuleId = "finance", Message = "hi" }, parentRunId);

    private static async Task<AgentRunRecord> FlushAsync(AgentRunRecorder recorder, CapturingAuditLog log)
    {
        await recorder.FlushAsync(log, NullLogger.Instance);
        return Assert.Single(log.Runs);
    }

    [Fact]
    public async Task An_undecided_turn_is_recorded_as_cancelled()
    {
        // The abandoned-enumeration case: nothing ever stamped an outcome, so the turn must not
        // masquerade as a success.
        var log = new CapturingAuditLog();

        var record = await FlushAsync(Recorder(), log);

        Assert.Equal(AgentRunOutcome.Cancelled, record.Outcome);
    }

    [Fact]
    public async Task The_first_outcome_wins()
    {
        // The output-guardrail case: a turn blocked by policy still runs to the end and would
        // otherwise overwrite its own verdict with Completed.
        var log = new CapturingAuditLog();
        var recorder = Recorder();

        recorder.Fail(AgentRunOutcome.BlockedBySecurity, "OutputBlocked", "pii:email");
        recorder.Complete();

        var record = await FlushAsync(recorder, log);
        Assert.Equal(AgentRunOutcome.BlockedBySecurity, record.Outcome);
        Assert.Equal("OutputBlocked", record.ErrorKind);
    }

    [Fact]
    public async Task A_completed_turn_carries_its_usage_and_timings()
    {
        var log = new CapturingAuditLog();
        var recorder = Recorder();

        recorder.Provider("OpenAI", "gpt-4o-mini");
        recorder.FirstToken();
        recorder.Tools(toolCallCount: 3, approvalCount: 1);
        recorder.Usage(inputTokens: 120, outputTokens: 40, totalTokens: 160);
        recorder.Complete();

        var record = await FlushAsync(recorder, log);
        Assert.Equal(AgentRunOutcome.Completed, record.Outcome);
        Assert.Equal("OpenAI", record.Provider);
        Assert.Equal("gpt-4o-mini", record.Model);
        Assert.Equal(3, record.ToolCallCount);
        Assert.Equal(1, record.ApprovalCount);
        Assert.Equal(160, record.TotalTokens);
        Assert.NotNull(record.FirstTokenMs);
    }

    [Fact]
    public async Task FirstToken_stamps_only_once()
    {
        // Time-to-first-token must measure the FIRST token, not the last one to arrive.
        var log = new CapturingAuditLog();
        var recorder = Recorder();

        recorder.FirstToken();
        await Task.Delay(200);
        recorder.FirstToken();

        var record = await FlushAsync(recorder, log);
        Assert.True(
            record.FirstTokenMs < 200,
            $"expected the first stamp to survive the later call, got {record.FirstTokenMs}ms");
        Assert.True(record.TotalMs >= 200, $"expected total to span the delay, got {record.TotalMs}ms");
    }

    [Fact]
    public async Task A_workflow_step_carries_its_parent()
    {
        var log = new CapturingAuditLog();
        var parentId = Guid.NewGuid();
        var recorder = Recorder(parentId);

        recorder.Complete();

        var record = await FlushAsync(recorder, log);
        Assert.Equal(parentId, record.ParentRunId);
    }

    [Fact]
    public async Task A_failing_audit_store_never_propagates_into_the_turn()
    {
        // Auditing is best-effort by contract everywhere else in the platform; the flush runs in a
        // finally, so a throw here would replace the turn's real outcome with an audit error.
        var recorder = Recorder();
        recorder.Complete();

        await recorder.FlushAsync(new ThrowingAuditLog(), NullLogger.Instance);
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AgentRunRecord> Runs { get; } = [];

        public Task RecordToolCallAsync(ToolCallAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordAuthEventAsync(AuthAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordEntityChangesAsync(IReadOnlyCollection<EntityChangeAuditEntry> entries, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordTokenUsageAsync(TokenUsageRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordAgentRunAsync(AgentRunRecord record, CancellationToken cancellationToken = default)
        {
            Runs.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAuditLog : IAuditLog
    {
        public Task RecordToolCallAsync(ToolCallAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordAuthEventAsync(AuthAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordEntityChangesAsync(IReadOnlyCollection<EntityChangeAuditEntry> entries, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordTokenUsageAsync(TokenUsageRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordAgentRunAsync(AgentRunRecord record, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("audit store is down");
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? UserId => Guid.Empty;
        public string? Subject => "test";
        public string? DisplayName => "Test User";
        public Guid? TenantId => Guid.Empty;
        public bool IsAuthenticated => true;
        public IReadOnlySet<string> Permissions => new HashSet<string>();
        public bool HasPermission(string permission) => true;
    }
}

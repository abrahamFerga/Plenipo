using Plenipo.Application.Auditing;
using Plenipo.Infrastructure.Auditing;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// The no-loss guarantee of the durable audit outbox, end to end: when the direct audit-store write fails
/// (a simulated outage), the record is captured in the outbox rather than dropped — and once the store
/// recovers, applying the captured message reconstitutes the exact record there. The happy path leaves the
/// outbox untouched.
/// </summary>
public sealed class AuditOutboxFallbackTests
{
    [Fact]
    public async Task AuditDbOutage_DefersToOutbox_AndRecoversIntoTheAuditStore()
    {
        using var outbox = NewOutbox();

        // The audit store is "down": its SaveChanges throws. The record must not be lost.
        using (var failingAudit = NewAudit(throwing: true))
        {
            var log = new AuditLog(failingAudit, outbox, NullLogger<AuditLog>.Instance);
            await log.RecordToolCallAsync(new ToolCallAuditEntry
            {
                TenantId = Guid.NewGuid(),
                ModuleId = "finance",
                ToolName = "summarize_spending",
                Permission = "tools.finance.summarize_spending",
                Success = true,
            });
        }

        // It was deferred to the durable outbox, not dropped.
        var pending = await outbox.Messages.ToListAsync();
        Assert.Single(pending);
        Assert.Equal(AuditRecordKind.ToolCall, pending[0].Kind);

        // Recovery: the store is back; applying the captured message reconstitutes the exact record.
        using var recoveredAudit = NewAudit(throwing: false);
        AuditOutboxSerializer.Apply(pending[0], recoveredAudit);
        await recoveredAudit.SaveChangesAsync();

        var stored = await recoveredAudit.ToolCalls.SingleAsync();
        Assert.Equal("summarize_spending", stored.ToolName);
        Assert.Equal("tools.finance.summarize_spending", stored.Permission);
    }

    [Fact]
    public async Task SuccessfulAuditWrite_DoesNotTouchTheOutbox()
    {
        using var outbox = NewOutbox();
        using var audit = NewAudit(throwing: false);
        var log = new AuditLog(audit, outbox, NullLogger<AuditLog>.Instance);

        await log.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = Guid.NewGuid(),
            EventType = AuthAuditEventType.SignIn,
        });

        Assert.Empty(await outbox.Messages.ToListAsync());
        Assert.Single(await audit.AuthEvents.ToListAsync());
    }

    private static AuditDbContext NewAudit(bool throwing)
    {
        var builder = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase($"audit-{Guid.NewGuid()}");
        if (throwing)
        {
            builder.AddInterceptors(new ThrowingSaveInterceptor());
        }
        return new AuditDbContext(builder.Options);
    }

    private static OutboxDbContext NewOutbox() =>
        new(new DbContextOptionsBuilder<OutboxDbContext>()
            .UseInMemoryDatabase($"outbox-{Guid.NewGuid()}")
            .Options);

    /// <summary>Makes any SaveChanges on the context throw — a stand-in for an audit-DB outage.</summary>
    private sealed class ThrowingSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated audit-DB outage.");

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result) =>
            throw new InvalidOperationException("Simulated audit-DB outage.");
    }
}

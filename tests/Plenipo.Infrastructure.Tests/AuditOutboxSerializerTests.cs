using System.Text.Json;
using Plenipo.Application.Auditing;
using Plenipo.Application.Usage;
using Plenipo.Infrastructure.Auditing;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// The durable audit outbox is only useful if a deferred record survives the JSON round-trip intact. These
/// cover each audit-record kind (including enum fidelity and the entity-change batch) plus the unknown-kind
/// guard the processor relies on.
/// </summary>
public sealed class AuditOutboxSerializerTests
{
    [Fact]
    public void ToolCall_RoundTrips()
    {
        var entry = new ToolCallAuditEntry
        {
            TenantId = Guid.NewGuid(),
            ModuleId = "finance",
            ToolName = "summarize_spending",
            Permission = "tools.finance.summarize_spending",
            Success = true,
            DurationMs = 42,
        };

        var message = AuditOutboxSerializer.ForToolCall(entry);
        Assert.Equal(AuditRecordKind.ToolCall, message.Kind);

        var back = JsonSerializer.Deserialize<ToolCallAuditEntry>(message.PayloadJson)!;
        Assert.Equal(entry.Id, back.Id);
        Assert.Equal("summarize_spending", back.ToolName);
        Assert.Equal("tools.finance.summarize_spending", back.Permission);
        Assert.True(back.Success);
        Assert.Equal(42, back.DurationMs);
    }

    [Fact]
    public void AuthEvent_RoundTrips_PreservingTheEventTypeEnum()
    {
        var entry = new AuthAuditEntry
        {
            TenantId = Guid.NewGuid(),
            EventType = AuthAuditEventType.RolePermissionsChanged,
            Detail = "role 'user': granted chat.use",
        };

        var message = AuditOutboxSerializer.ForAuthEvent(entry);
        var back = JsonSerializer.Deserialize<AuthAuditEntry>(message.PayloadJson)!;

        Assert.Equal(AuthAuditEventType.RolePermissionsChanged, back.EventType);
        Assert.Equal(entry.Detail, back.Detail);
    }

    [Fact]
    public void EntityChanges_RoundTrip_AsABatch()
    {
        var entries = new[]
        {
            new EntityChangeAuditEntry { EntityType = "TenantModule", EntityId = "abc", Kind = EntityChangeKind.Modified, ChangesJson = "{}" },
            new EntityChangeAuditEntry { EntityType = "RolePermission", EntityId = "def", Kind = EntityChangeKind.Created },
        };

        var message = AuditOutboxSerializer.ForEntityChanges(entries);
        Assert.Equal(AuditRecordKind.EntityChange, message.Kind);

        var back = JsonSerializer.Deserialize<List<EntityChangeAuditEntry>>(message.PayloadJson)!;
        Assert.Equal(2, back.Count);
        Assert.Equal(EntityChangeKind.Modified, back[0].Kind);
        Assert.Equal("TenantModule", back[0].EntityType);
    }

    [Fact]
    public void TokenUsage_RoundTrips()
    {
        var record = new TokenUsageRecord
        {
            TenantId = Guid.NewGuid(),
            ModuleId = "finance",
            InputTokens = 100,
            OutputTokens = 50,
            TotalTokens = 150,
            Provider = "Mock",
            Model = "mock",
        };

        var message = AuditOutboxSerializer.ForTokenUsage(record);
        var back = JsonSerializer.Deserialize<TokenUsageRecord>(message.PayloadJson)!;

        Assert.Equal(150, back.TotalTokens);
        Assert.Equal("finance", back.ModuleId);
    }

    [Fact]
    public void Apply_UnknownKind_Throws()
    {
        var message = new AuditOutboxMessage { Kind = "Nonsense", PayloadJson = "{}" };
        Assert.Throws<InvalidOperationException>(() => AuditOutboxSerializer.Apply(message, audit: null!));
    }
}

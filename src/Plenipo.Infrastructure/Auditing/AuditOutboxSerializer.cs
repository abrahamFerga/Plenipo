using System.Text.Json;
using Plenipo.Application.Auditing;
using Plenipo.Application.Usage;
using Plenipo.Infrastructure.Persistence;

namespace Plenipo.Infrastructure.Auditing;

/// <summary>
/// Serializes audit entries into <see cref="AuditOutboxMessage"/>s and, on the way back out, materializes a
/// message into the right audit-store table. Pure and side-effect-light (except <see cref="Apply"/>, which
/// only stages adds on the supplied context) so it can be unit-tested without a database.
/// </summary>
public static class AuditOutboxSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    public static AuditOutboxMessage ForToolCall(ToolCallAuditEntry entry) => Create(AuditRecordKind.ToolCall, entry);
    public static AuditOutboxMessage ForAuthEvent(AuthAuditEntry entry) => Create(AuditRecordKind.AuthEvent, entry);
    public static AuditOutboxMessage ForTokenUsage(TokenUsageRecord record) => Create(AuditRecordKind.TokenUsage, record);

    public static AuditOutboxMessage ForEntityChanges(IReadOnlyCollection<EntityChangeAuditEntry> entries) =>
        Create(AuditRecordKind.EntityChange, entries);

    private static AuditOutboxMessage Create<T>(string kind, T payload) =>
        new() { Kind = kind, PayloadJson = JsonSerializer.Serialize(payload, Options) };

    /// <summary>Deserializes a message and stages its record(s) onto the audit context. Does not save.</summary>
    public static void Apply(AuditOutboxMessage message, AuditDbContext audit)
    {
        ArgumentNullException.ThrowIfNull(message);

        switch (message.Kind)
        {
            case AuditRecordKind.ToolCall:
                audit.ToolCalls.Add(Deserialize<ToolCallAuditEntry>(message.PayloadJson));
                break;
            case AuditRecordKind.AuthEvent:
                audit.AuthEvents.Add(Deserialize<AuthAuditEntry>(message.PayloadJson));
                break;
            case AuditRecordKind.EntityChange:
                audit.EntityChanges.AddRange(Deserialize<List<EntityChangeAuditEntry>>(message.PayloadJson));
                break;
            case AuditRecordKind.TokenUsage:
                audit.TokenUsage.Add(Deserialize<TokenUsageRecord>(message.PayloadJson));
                break;
            default:
                throw new InvalidOperationException($"Unknown audit outbox kind '{message.Kind}'.");
        }
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException($"Audit outbox payload deserialized to null for {typeof(T).Name}.");
}

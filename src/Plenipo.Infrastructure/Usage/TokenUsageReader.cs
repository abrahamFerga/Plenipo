using Plenipo.Application.Usage;
using Plenipo.Core.Identity;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Usage;

/// <summary>
/// Reads accumulated token usage from the append-only audit store. The audit context has no global
/// tenant filter, so the tenant is applied explicitly here.
/// </summary>
public sealed class TokenUsageReader(AuditDbContext db, ICurrentUser currentUser) : ITokenUsageReader
{
    public async Task<long> GetConversationTotalAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var tenantId = currentUser.TenantId ?? Guid.Empty;
        return await db.TokenUsage
            .Where(u => u.TenantId == tenantId && u.ConversationId == conversationId)
            .SumAsync(u => (long?)u.TotalTokens, cancellationToken) ?? 0L;
    }

    public async Task<long> GetTenantMonthTotalAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var tenantId = currentUser.TenantId ?? Guid.Empty;
        var monthStart = new DateTimeOffset(nowUtc.UtcDateTime.Year, nowUtc.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return await db.TokenUsage
            .Where(u => u.TenantId == tenantId && u.OccurredAt >= monthStart)
            .SumAsync(u => (long?)u.TotalTokens, cancellationToken) ?? 0L;
    }
}

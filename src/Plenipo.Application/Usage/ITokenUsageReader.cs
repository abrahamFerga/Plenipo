namespace Plenipo.Application.Usage;

/// <summary>Reads accumulated token usage from the audit store (for budget enforcement and reporting).</summary>
public interface ITokenUsageReader
{
    /// <summary>Total tokens consumed across all completed turns of a conversation (tenant-scoped).</summary>
    public Task<long> GetConversationTotalAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Total tokens the tenant consumed in the UTC calendar month containing <paramref name="nowUtc"/>.</summary>
    public Task<long> GetTenantMonthTotalAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

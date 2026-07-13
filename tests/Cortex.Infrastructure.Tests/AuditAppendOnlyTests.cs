using Cortex.Application.Auditing;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Infrastructure.Tests;

public sealed class AuditAppendOnlyTests
{
    [Fact]
    public async Task AuditContext_RejectsUpdatesAndDeletes()
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase($"audit-append-only-{Guid.NewGuid():N}")
            .Options;
        await using var db = new AuditDbContext(options);
        var entry = new AuthAuditEntry
        {
            TenantId = Guid.NewGuid(),
            EventType = AuthAuditEventType.UserProvisioned,
        };
        db.AuthEvents.Add(entry);
        await db.SaveChangesAsync();

        db.Entry(entry).State = EntityState.Modified;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        db.Entry(entry).State = EntityState.Unchanged;
        db.AuthEvents.Remove(entry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}

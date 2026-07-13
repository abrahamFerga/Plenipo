using System.Net.Http.Json;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The org-level cost ceiling end to end: with a tiny monthly budget, the first turn runs (and
/// crosses the ceiling), the next turn is refused tenant-wide, and the tenant's admins get the
/// exhaustion alert in their notification inbox.
/// </summary>
[Collection("api")]
public sealed class MonthlyBudgetIntegrationTests(IntegrationFixture fixture)
{
    private sealed record NotificationDto(
        Guid Id, string Category, string Title, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

    [Fact]
    public async Task ExhaustedMonthlyBudget_RefusesChat_AndAlertsTenantAdmins()
    {
        // tenant_admin both configures the budget and (being an admin) receives the alert.
        using var admin = fixture.ClientFor("tenant_admin");
        try
        {
            // Other tests in the shared fixture have already consumed month tokens; a budget of
            // current+1 makes THIS test's first turn the one that crosses the ceiling.
            long budget;
            using (var scope = fixture.Factory.Services.CreateScope())
            {
                var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var tenantId = (await platform.Tenants.FirstAsync(t => t.Slug == "dev")).Id;
                var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
                var monthStart = new DateTimeOffset(
                    DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
                var consumed = await audit.TokenUsage
                    .Where(u => u.TenantId == tenantId && u.OccurredAt >= monthStart)
                    .SumAsync(u => (long?)u.TotalTokens) ?? 0L;
                budget = consumed + 1;
            }

            using (var put = await admin.PutAsJsonAsync("/api/admin/ai-settings",
                new { maxMonthlyTokens = budget }))
            {
                put.EnsureSuccessStatusCode();
            }

            // First turn: prior month usage is 0 < 1, so it runs — and its recorded usage crosses
            // the whole budget (80% and 100% in one step → single exhaustion alert).
            using (var first = await admin.PostAsJsonAsync("/api/agui/finance",
                new { messages = new[] { new { id = "m1", role = "user", content = "Hello budget" } } }))
            {
                first.EnsureSuccessStatusCode();
                Assert.DoesNotContain("RUN_ERROR", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // Second turn: the tenant-wide ceiling is reached — refused regardless of conversation.
            using (var second = await admin.PostAsJsonAsync("/api/agui/finance",
                new { messages = new[] { new { id = "m1", role = "user", content = "And again" } } }))
            {
                second.EnsureSuccessStatusCode();
                var sse = await second.Content.ReadAsStringAsync();
                Assert.Contains("RUN_ERROR", sse, StringComparison.Ordinal);
                Assert.Contains("monthly token budget", sse, StringComparison.OrdinalIgnoreCase);
            }

            var inbox = await admin.GetFromJsonAsync<List<NotificationDto>>("/api/notifications/");
            Assert.Contains(inbox!, n => n.Category == "budget" && n.Title.Contains("exhausted", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            // Clear the override so the shared fixture's other chat tests aren't budget-refused.
            using var reset = await admin.PutAsJsonAsync("/api/admin/ai-settings", new { });
            reset.EnsureSuccessStatusCode();
        }
    }
}

using Cortex.Application.Approvals;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Context;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Sample.Host.IntegrationTests;

[Collection("api")]
public sealed class ApprovalConcurrencyTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Only_one_concurrent_approver_can_claim_a_pending_action()
    {
        Guid tenantId;
        Guid approvalId;
        using (var seedScope = fixture.Factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            tenantId = await db.Tenants.Where(t => t.Slug == "dev").Select(t => t.Id).SingleAsync();
            seedScope.ServiceProvider.GetRequiredService<RequestContext>().SetTenant(tenantId);

            var approval = new PendingApproval
            {
                TenantId = tenantId,
                ConversationId = Guid.NewGuid(),
                ModuleId = "finance",
                ToolName = "record_transaction",
            };
            db.PendingApprovals.Add(approval);
            await db.SaveChangesAsync();
            approvalId = approval.Id;
        }

        async Task<PendingApproval?> ClaimAsync(string display)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<RequestContext>().SetTenant(tenantId);
            return await scope.ServiceProvider.GetRequiredService<IApprovalStore>()
                .TryBeginExecutionAsync(approvalId, Guid.NewGuid(), display);
        }

        var claims = await Task.WhenAll(ClaimAsync("approver-a"), ClaimAsync("approver-b"));

        Assert.Single(claims, claim => claim is not null);
        Assert.Single(claims, claim => claim is null);
    }
}

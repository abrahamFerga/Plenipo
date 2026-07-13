using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal;
using Plenipo.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The matter brief: one call composes everything the matter carries — parties, open deadlines
/// (overdue flagged), open tasks, time totals, documents — and the ethical wall makes a walled
/// matter's brief indistinguishable from a missing matter to outsiders.
/// </summary>
[Collection("api")]
public sealed class MatterOverviewTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Overview_ComposesEverySection_AndWallsHold()
    {
        var matterName = $"Brief target {Guid.NewGuid():N}"[..26];
        var walledName = $"Brief walled {Guid.NewGuid():N}"[..26];

        using var scope = await DevUserScopeAsync();
        var legal = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenantId = (await platform.Tenants.FirstAsync(t => t.Slug == "dev")).Id;

        var matter = new Matter { TenantId = tenantId, Name = matterName, ClientName = "Vandelay" };
        var walled = new Matter
        {
            TenantId = tenantId, Name = walledName,
            RestrictedUserIdsJson = $"[\"{Guid.NewGuid()}\"]",
        };
        legal.Matters.AddRange(matter, walled);
        legal.MatterParties.Add(new MatterParty { TenantId = tenantId, MatterId = matter.Id, Name = "Kruger", Role = PartyRole.Adverse });
        legal.MatterDeadlines.Add(new MatterDeadline
        {
            TenantId = tenantId, MatterId = matter.Id, Title = "Answer due",
            DueAt = DateTimeOffset.UtcNow.AddDays(-1), // overdue → flagged
        });
        legal.MatterTasks.Add(new MatterTask { TenantId = tenantId, MatterId = matter.Id, Title = "Draft the motion", AssignedTo = "Maria" });
        legal.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenantId, MatterId = matter.Id, Hours = 1.5m, Description = "Research",
            WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow),
        });
        legal.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenantId, MatterId = matter.Id, Hours = 0.5m, Description = "Internal sync",
            WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow), Billable = false,
        });
        legal.MatterDocuments.Add(new MatterDocument
        {
            TenantId = tenantId, MatterId = matter.Id, FileId = Guid.NewGuid(), FileName = "complaint.pdf",
        });
        await legal.SaveChangesAsync();

        var tools = scope.ServiceProvider.GetRequiredService<MatterTools>();
        var brief = await tools.GetMatterOverview(matterName);

        Assert.Contains($"MATTER BRIEF: {matterName}", brief);
        Assert.Contains("Client: Vandelay", brief);
        Assert.Contains("Kruger (ADVERSE)", brief);
        Assert.Contains("Answer due", brief);
        Assert.Contains("OVERDUE", brief);
        Assert.Contains("Draft the motion (assigned to Maria)", brief);
        Assert.Contains("2h total, 1.5h billable", brief);
        Assert.Contains("complaint.pdf", brief);

        // Outside the wall, the brief of a walled matter is a not-found — no existence leak.
        Assert.Contains("No matter named", await tools.GetMatterOverview(walledName));
    }

    /// <summary>A scope acting as the JIT-provisioned dev user (tenant + user + wildcard permissions).</summary>
    private async Task<IServiceScope> DevUserScopeAsync()
    {
        using (var warmup = fixture.ClientFor("user"))
        {
            (await warmup.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();
        }

        var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();

        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        var user = await db.Users.FirstAsync(u => u.Subject == "it-user");
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
        return scope;
    }
}

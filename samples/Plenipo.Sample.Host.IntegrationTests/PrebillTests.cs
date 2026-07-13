using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal;
using Plenipo.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The pre-bill closes the billing loop: time entries become a PDF filed on the matter in one
/// step (render + store + attach can't leave an orphan), the date range filters entries, and the
/// totals split billable from non-billable.
/// </summary>
[Collection("api")]
public sealed class PrebillTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Prebill_FilesPdfOnMatter_WithRangeAndBillableTotals()
    {
        var matterName = $"Prebill target {Guid.NewGuid():N}"[..28];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using var scope = await DevUserScopeAsync();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenantId = (await platform.Tenants.FirstAsync(t => t.Slug == "dev")).Id;

        var legal = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
        var matter = new Matter { TenantId = tenantId, Name = matterName, ClientName = "Vandelay" };
        legal.Matters.Add(matter);
        legal.TimeEntries.AddRange(
            new TimeEntry
            {
                TenantId = tenantId, MatterId = matter.Id, Hours = 2m, Description = "Drafted the NDA",
                WorkedOn = today, UserDisplay = "Test Attorney",
            },
            new TimeEntry
            {
                TenantId = tenantId, MatterId = matter.Id, Hours = 0.5m, Description = "Internal sync",
                WorkedOn = today, Billable = false,
            },
            new TimeEntry
            {
                TenantId = tenantId, MatterId = matter.Id, Hours = 3m, Description = "Ancient research",
                WorkedOn = today.AddDays(-60), // outside the requested period below
            });
        await legal.SaveChangesAsync();

        var tools = scope.ServiceProvider.GetRequiredService<MatterTools>();
        var result = await tools.ExportPrebill(matterName, fromDate: today.AddDays(-7).ToString("yyyy-MM-dd"));

        // The range excluded the 60-day-old entry: 2h billable + 0.5h non-billable remain.
        Assert.Contains("2 entr(ies)", result);
        Assert.Contains("2h billable", result);
        Assert.Contains("0.5h non-billable", result);
        Assert.Contains("Filed pre-bill", result);

        // The PDF landed on the matter as a document with the pre-bill note.
        var document = await legal.MatterDocuments.SingleAsync(d => d.MatterId == matter.Id);
        Assert.StartsWith("prebill-", document.FileName, StringComparison.Ordinal);
        Assert.Contains("2h billable", document.Note);

        // Nothing to bill → no orphan artifacts.
        var empty = await tools.ExportPrebill(matterName, fromDate: today.AddDays(1).ToString("yyyy-MM-dd"));
        Assert.Contains("nothing to pre-bill", empty);
        Assert.Equal(1, await legal.MatterDocuments.CountAsync(d => d.MatterId == matter.Id));
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

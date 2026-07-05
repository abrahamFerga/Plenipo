using Cortex.Infrastructure.Persistence;
using Cortex.Modules.Legal;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// The client status letter: composed from real matter state (recent completions, upcoming dates,
/// hours), filed on the matter as an explicit DRAFT for attorney review — never presented as sent.
/// </summary>
[Collection("api")]
public sealed class StatusUpdateTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task StatusUpdate_ComposesFromMatterState_AndFilesAsDraft()
    {
        var matterName = $"Status target {Guid.NewGuid():N}"[..26];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        using var scope = await DevUserScopeAsync();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenantId = (await platform.Tenants.FirstAsync(t => t.Slug == "dev")).Id;

        var legal = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
        var matter = new Matter { TenantId = tenantId, Name = matterName, ClientName = "Vandelay" };
        legal.Matters.Add(matter);
        legal.MatterDeadlines.AddRange(
            new MatterDeadline
            {
                TenantId = tenantId, MatterId = matter.Id, Title = "Filed the answer",
                DueAt = DateTimeOffset.UtcNow.AddDays(-5), CompletedAt = DateTimeOffset.UtcNow.AddDays(-4),
            },
            new MatterDeadline
            {
                TenantId = tenantId, MatterId = matter.Id, Title = "Mediation session",
                DueAt = DateTimeOffset.UtcNow.AddDays(21),
            });
        legal.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenantId, MatterId = matter.Id, Hours = 3.5m, Description = "Answer and exhibits",
            WorkedOn = today.AddDays(-4),
        });
        await legal.SaveChangesAsync();

        var tools = scope.ServiceProvider.GetRequiredService<MatterTools>();
        var result = await tools.DraftStatusUpdate(matterName);

        Assert.Contains("Filed draft status update", result);
        Assert.Contains("1 completed item(s)", result);
        Assert.Contains("1 upcoming date(s)", result);
        Assert.Contains("3.5h in the last 30 days", result);
        Assert.Contains("Review before sending", result);

        var document = await legal.MatterDocuments.SingleAsync(d => d.MatterId == matter.Id);
        Assert.StartsWith("status-update-", document.FileName, StringComparison.Ordinal);
        Assert.Contains("draft", document.Note, StringComparison.OrdinalIgnoreCase);
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
        var context = scope.ServiceProvider.GetRequiredService<Cortex.Infrastructure.Context.RequestContext>();

        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        var user = await db.Users.FirstAsync(u => u.Subject == "it-user");
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
        return scope;
    }
}

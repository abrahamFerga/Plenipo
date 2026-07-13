using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal;
using Plenipo.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Matter close-out: the completeness check blocks a close while deadlines/tasks are open, a
/// forced close warns and — critically — silences the reminder scanner for that matter (a closed
/// file must not page anyone), and reopening brings everything back.
/// </summary>
[Collection("api")]
public sealed class MatterCloseoutTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Closeout_BlocksOnOpenWork_ForcedCloseSilencesReminders_ReopenRestores()
    {
        var matterName = $"Closeout target {Guid.NewGuid():N}"[..28];
        var owner = Guid.NewGuid();

        using var scope = await DevUserScopeAsync();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenantId = (await platform.Tenants.FirstAsync(t => t.Slug == "dev")).Id;

        var legal = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
        var matter = new Matter { TenantId = tenantId, Name = matterName };
        legal.Matters.Add(matter);
        legal.MatterDeadlines.Add(new MatterDeadline
        {
            TenantId = tenantId, MatterId = matter.Id, Title = "Discovery cutoff",
            DueAt = DateTimeOffset.UtcNow.AddDays(1), OwnerUserId = owner, // inside the reminder window
        });
        legal.MatterTasks.Add(new MatterTask { TenantId = tenantId, MatterId = matter.Id, Title = "Collect exhibits" });
        await legal.SaveChangesAsync();

        var tools = scope.ServiceProvider.GetRequiredService<MatterTools>();

        // The completeness check names what blocks the close.
        var refused = await tools.CloseMatter(matterName);
        Assert.Contains("CANNOT CLOSE", refused);
        Assert.Contains("1 open deadline(s)", refused);
        Assert.Contains("1 open task(s)", refused);

        // Forced close: warns, closes, and the reminder scanner skips the closed matter's deadline.
        var forced = await tools.CloseMatter(matterName, force: true);
        Assert.Contains("WARNING (forced)", forced);

        await DeadlineReminderService.ScanOnceAsync(scope.ServiceProvider, DateTimeOffset.UtcNow);
        var reminders = await platform.UserNotifications.IgnoreQueryFilters()
            .CountAsync(n => n.UserId == owner && n.Category == "legal.deadline");
        Assert.Equal(0, reminders); // a closed file never pages anyone

        // The closed matter's items leave the open lists.
        Assert.DoesNotContain("Discovery cutoff", await tools.ListDeadlines());
        Assert.DoesNotContain("Collect exhibits", await tools.ListTasks());

        // Reopen: items are active again, and the still-open deadline reminds on the next scan.
        Assert.Contains("Reopened", await tools.ReopenMatter(matterName));
        Assert.Contains("Discovery cutoff", await tools.ListDeadlines());

        await DeadlineReminderService.ScanOnceAsync(scope.ServiceProvider, DateTimeOffset.UtcNow);
        reminders = await platform.UserNotifications.IgnoreQueryFilters()
            .CountAsync(n => n.UserId == owner && n.Category == "legal.deadline");
        Assert.Equal(1, reminders);

        // With the work done, a clean close needs no force.
        await tools.CompleteDeadline(matterName, "Discovery cutoff");
        await tools.CompleteTask(matterName, "Collect exhibits");
        Assert.Contains("Nothing was left open", await tools.CloseMatter(matterName));
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

using System.Net.Http.Json;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Time tracking end to end: entries surface in the Time tab with matter and billable totals, a
/// walled matter's entries stay invisible to outsiders, and the agent's list_time is a read with
/// no approval friction (log_time is deliberately un-gated quick capture — pinned by the module's
/// manifest test).
/// </summary>
[Collection("api")]
public sealed class TimeTrackingTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task TimeEntries_SurfaceInTab_AndWallsHoldEverywhere()
    {
        var narrative = $"Drafted NDA {Guid.NewGuid():N}"[..24];
        var walledNarrative = $"Screened work {Guid.NewGuid():N}"[..26];

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenantId = (await platform.Tenants.FirstAsync(t => t.Slug == "dev")).Id;

            var legal = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
            var open = new Matter { TenantId = tenantId, Name = $"Time open {Guid.NewGuid():N}"[..24] };
            var walled = new Matter
            {
                TenantId = tenantId,
                Name = $"Time walled {Guid.NewGuid():N}"[..26],
                RestrictedUserIdsJson = $"[\"{Guid.NewGuid()}\"]",
            };
            legal.Matters.AddRange(open, walled);
            legal.TimeEntries.Add(new TimeEntry
            {
                TenantId = tenantId, MatterId = open.Id, Hours = 1.5m, Description = narrative,
                WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow), UserDisplay = "Test Attorney",
            });
            legal.TimeEntries.Add(new TimeEntry
            {
                TenantId = tenantId, MatterId = walled.Id, Hours = 2m, Description = walledNarrative,
                WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            });
            await legal.SaveChangesAsync();
        }

        using var client = fixture.ClientFor("system_admin");
        using var response = await client.GetAsync("/api/legal/time");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains(narrative, body, StringComparison.Ordinal);
        // The walled matter's entry is invisible to a caller outside the wall — even a wildcard admin.
        Assert.DoesNotContain(walledNarrative, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_AnswersWhatDidIWorkOn_WithoutApprovalFriction()
    {
        using var client = fixture.ClientFor("system_admin");
        using var chat = await client.PostAsJsonAsync("/api/agui/legal",
            new { messages = new[] { new { id = "m1", role = "user", content = "List my time for the last two weeks" } } });
        chat.EnsureSuccessStatusCode();
        var run = Evals.EvalRun.Parse(await chat.Content.ReadAsStringAsync());

        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
        Assert.Contains("list_time", run.ToolCalls);
        Assert.DoesNotContain("approval_required", run.CustomEvents);
    }
}

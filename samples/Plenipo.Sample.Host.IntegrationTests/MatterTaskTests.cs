using System.Net.Http.Json;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Matter tasks end to end: open tasks surface in the Tasks tab (dated first) with assignee and
/// matter, walled matters keep theirs invisible, and the agent lists tasks without approval
/// friction (adding/completing stay approval-gated — pinned by the module's manifest test).
/// </summary>
[Collection("api")]
public sealed class MatterTaskTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Tasks_SurfaceInTab_AndWallsHold()
    {
        var openTask = $"Draft motion {Guid.NewGuid():N}"[..24];
        var walledTask = $"Screened task {Guid.NewGuid():N}"[..26];

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenantId = (await platform.Tenants.FirstAsync(t => t.Slug == "dev")).Id;

            var legal = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
            var open = new Matter { TenantId = tenantId, Name = $"Tasks open {Guid.NewGuid():N}"[..24] };
            var walled = new Matter
            {
                TenantId = tenantId,
                Name = $"Tasks walled {Guid.NewGuid():N}"[..26],
                RestrictedUserIdsJson = $"[\"{Guid.NewGuid()}\"]",
            };
            legal.Matters.AddRange(open, walled);
            legal.MatterTasks.Add(new MatterTask
            {
                TenantId = tenantId, MatterId = open.Id, Title = openTask,
                AssignedTo = "Maria", DueOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)),
            });
            legal.MatterTasks.Add(new MatterTask
            {
                TenantId = tenantId, MatterId = walled.Id, Title = walledTask,
            });
            await legal.SaveChangesAsync();
        }

        using var client = fixture.ClientFor("system_admin");
        using var response = await client.GetAsync("/api/legal/tasks");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains(openTask, body, StringComparison.Ordinal);
        Assert.Contains("Maria", body, StringComparison.Ordinal);
        Assert.DoesNotContain(walledTask, body, StringComparison.Ordinal); // the wall holds
    }

    [Fact]
    public async Task Agent_ListsOpenTasks_WithoutApprovalFriction()
    {
        using var client = fixture.ClientFor("system_admin");
        using var chat = await client.PostAsJsonAsync("/api/agui/legal",
            new { messages = new[] { new { id = "m1", role = "user", content = "List the open tasks" } } });
        chat.EnsureSuccessStatusCode();
        var run = Evals.EvalRun.Parse(await chat.Content.ReadAsStringAsync());

        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
        Assert.Contains("list_tasks", run.ToolCalls);
        Assert.DoesNotContain("approval_required", run.CustomEvents);
    }
}

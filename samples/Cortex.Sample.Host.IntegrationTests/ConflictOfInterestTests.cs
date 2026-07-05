using System.Net.Http.Json;
using Cortex.Infrastructure.Persistence;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// Conflicts at intake, end to end: the agent's check finds recorded parties across the firm's
/// matters — and a match on a WALLED matter is reported anonymously (a screened hit), never by
/// name, so the conflicts process works without breaching the ethical wall.
/// </summary>
[Collection("api")]
public sealed class ConflictOfInterestTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task ConflictCheck_FindsAdverseParty_AndScreensWalledMatters()
    {
        var openParty = $"Initech-{Guid.NewGuid():N}"[..16];
        var walledParty = $"Globex-{Guid.NewGuid():N}"[..16];
        var openMatterName = $"Conflicts open {Guid.NewGuid():N}"[..28];
        var walledMatterName = $"Conflicts walled {Guid.NewGuid():N}"[..30];

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenantId = (await platform.Tenants.FirstAsync(t => t.Slug == "dev")).Id;

            var legal = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
            var open = new Matter { TenantId = tenantId, Name = openMatterName };
            // Walled to a user who is NOT the caller — the caller must get an anonymous hit only.
            var walled = new Matter
            {
                TenantId = tenantId,
                Name = walledMatterName,
                RestrictedUserIdsJson = $"[\"{Guid.NewGuid()}\"]",
            };
            legal.Matters.AddRange(open, walled);
            legal.MatterParties.Add(new MatterParty
            {
                TenantId = tenantId, MatterId = open.Id, Name = openParty, Role = PartyRole.Adverse,
            });
            legal.MatterParties.Add(new MatterParty
            {
                TenantId = tenantId, MatterId = walled.Id, Name = walledParty, Role = PartyRole.Client,
            });
            await legal.SaveChangesAsync();
        }

        using var client = fixture.ClientFor("system_admin");

        // Visible matter: the hit names the party, its role, and the matter.
        var openRun = await ChatAsync(client, $"Run a conflict check for {openParty}");
        Assert.Contains("check_conflicts", openRun.ToolCalls);
        Assert.Contains("ADVERSE", openRun.AssistantText, StringComparison.Ordinal);
        Assert.Contains(openMatterName, openRun.AssistantText, StringComparison.Ordinal);

        // Walled matter: a screened, anonymous hit — neither the matter nor the party leaks.
        var walledRun = await ChatAsync(client, $"Run a conflict check for {walledParty}");
        Assert.Contains("RESTRICTED", walledRun.AssistantText, StringComparison.Ordinal);
        Assert.DoesNotContain(walledMatterName, walledRun.AssistantText, StringComparison.Ordinal);

        // No match: an explicit all-clear, with the record-keeping caveat.
        var clearRun = await ChatAsync(client, $"Run a conflict check for Wayne-{Guid.NewGuid():N}");
        Assert.Contains("CONFLICT CHECK CLEAR", clearRun.AssistantText, StringComparison.Ordinal);
    }

    private static async Task<Evals.EvalRun> ChatAsync(HttpClient client, string message)
    {
        using var chat = await client.PostAsJsonAsync("/api/agui/legal",
            new { messages = new[] { new { id = "m1", role = "user", content = message } } });
        chat.EnsureSuccessStatusCode();
        var run = Evals.EvalRun.Parse(await chat.Content.ReadAsStringAsync());
        Assert.DoesNotContain("RUN_ERROR", run.EventTypes);
        return run;
    }
}

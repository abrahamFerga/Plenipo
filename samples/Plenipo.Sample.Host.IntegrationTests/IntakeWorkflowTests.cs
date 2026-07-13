using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The intake workflow (legal v2 item 15) end to end through the real tool surface, exactly as
/// the module's INTAKE WORKFLOW instructions prescribe it to the agent: conflict check → open the
/// matter → record the parties → draft the engagement letter. And the flywheel that makes intake
/// worth the ceremony: the parties recorded at step 3 are what the NEXT intake's conflict check
/// finds.
/// </summary>
[Collection("api")]
public sealed class IntakeWorkflowTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Intake_ConflictCheck_Matter_Parties_EngagementLetter_ThenNextCheckSeesThem()
    {
        var client = $"Vandelay-{Guid.NewGuid():N}"[..18];
        var adverse = $"Kruger-{Guid.NewGuid():N}"[..17];
        var matterName = $"{client} acquisition";

        using var scope = await DevUserScopeAsync();
        var matters = scope.ServiceProvider.GetRequiredService<MatterTools>();
        var clauses = scope.ServiceProvider.GetRequiredService<LegalTools>();

        // (1) Conflict check on client + opposing party: clear — nothing recorded yet.
        Assert.Contains("CONFLICT CHECK CLEAR", await matters.CheckConflicts($"{client}; {adverse}"));

        // (2) Open the matter.
        Assert.Contains($"Created matter '{matterName}'", await matters.CreateMatter(matterName, client));

        // (3) Record the parties — this is what feeds every future conflict check.
        Assert.Contains("CLIENT", await matters.AddParty(matterName, client, "client"));
        Assert.Contains("ADVERSE", await matters.AddParty(matterName, adverse, "adverse", "opposing party in the acquisition"));

        // (4) Draft the engagement letter from the seeded template, firm + client as parties.
        var letter = await clauses.DraftClause("engagement letter", "The Firm LLP", client);
        Assert.Contains("ENGAGEMENT LETTER", letter);
        Assert.Contains(client, letter);
        Assert.Contains("SCOPE", letter);
        Assert.Contains("not legal advice", letter);

        // The flywheel: the very next intake that touches either party now gets a hit, with role
        // and matter — the reason intake insists on add_party.
        var recheck = await matters.CheckConflicts(adverse);
        Assert.Contains("potential conflict", recheck);
        Assert.Contains("ADVERSE", recheck);
        Assert.Contains(matterName, recheck);

        var clientRecheck = await matters.CheckConflicts(client);
        Assert.Contains("CLIENT", clientRecheck);
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

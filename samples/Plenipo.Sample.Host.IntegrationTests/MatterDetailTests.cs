using System.Text.Json;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The matter drill-down: the detail endpoint composes the working file (parties, open deadlines
/// with flags, open tasks, time totals, documents) as a generic detail document, and the ethical
/// wall makes a walled matter's detail a 404 — indistinguishable from missing.
/// </summary>
[Collection("api")]
public sealed class MatterDetailTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Detail_ComposesTheWorkingFile_AndWallsReturn404()
    {
        var matterName = $"Detail target {Guid.NewGuid():N}"[..26];

        Guid matterId, walledId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenantId = (await platform.Tenants.FirstAsync(t => t.Slug == "dev")).Id;

            var legal = scope.ServiceProvider.GetRequiredService<LegalDbContext>();
            var matter = new Matter { TenantId = tenantId, Name = matterName, ClientName = "Vandelay" };
            var walled = new Matter
            {
                TenantId = tenantId,
                Name = $"Detail walled {Guid.NewGuid():N}"[..26],
                RestrictedUserIdsJson = $"[\"{Guid.NewGuid()}\"]",
            };
            legal.Matters.AddRange(matter, walled);
            legal.MatterParties.Add(new MatterParty { TenantId = tenantId, MatterId = matter.Id, Name = "Kruger", Role = PartyRole.Adverse });
            legal.MatterDeadlines.Add(new MatterDeadline
            {
                TenantId = tenantId, MatterId = matter.Id, Title = "Answer due", DueAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            legal.MatterTasks.Add(new MatterTask { TenantId = tenantId, MatterId = matter.Id, Title = "Collect exhibits", AssignedTo = "Maria" });
            legal.TimeEntries.Add(new TimeEntry
            {
                TenantId = tenantId, MatterId = matter.Id, Hours = 2m, Description = "Research",
                WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow),
            });
            legal.MatterDocuments.Add(new MatterDocument
            {
                TenantId = tenantId, MatterId = matter.Id, FileId = Guid.NewGuid(), FileName = "complaint.pdf",
            });
            await legal.SaveChangesAsync();
            matterId = matter.Id;
            walledId = walled.Id;
        }

        using var client = fixture.ClientFor("system_admin");
        using var response = await client.GetAsync($"/api/legal/matters/{matterId}/detail");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(matterName, root.GetProperty("title").GetString());
        Assert.Contains("Client: Vandelay", root.GetProperty("subtitle").GetString());

        var sections = root.GetProperty("sections").EnumerateArray()
            .ToDictionary(s => s.GetProperty("heading").GetString()!);
        Assert.Contains("Kruger", sections["Parties"].GetProperty("rows")[0].GetProperty("name").GetString());
        Assert.Equal("OVERDUE", sections["Open deadlines"].GetProperty("rows")[0].GetProperty("status").GetString());
        Assert.Equal("Maria", sections["Open tasks"].GetProperty("rows")[0].GetProperty("assignedTo").GetString());
        Assert.Contains("2h total", sections["Time"].GetProperty("text").GetString());
        Assert.Equal("complaint.pdf", sections["Documents"].GetProperty("rows")[0].GetProperty("fileName").GetString());

        // Outside the wall: the detail of a walled matter is a 404 — no existence leak.
        using var walledResponse = await client.GetAsync($"/api/legal/matters/{walledId}/detail");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, walledResponse.StatusCode);
    }
}

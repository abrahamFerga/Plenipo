using System.Net.Http.Json;
using Plenipo.Application.Jobs;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Infrastructure.Rag;
using Plenipo.Modules.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Lane B end to end (plan phase 5), keyless via the local-folder connector: bind a matter to a
/// folder (Harvey-style, one binding per matter), the sync job imports the folder's files into the
/// file store, attaches them to the matter, and indexes them into the matter's knowledge
/// collection; re-syncing is incremental — new/changed files only, no duplicates.
/// </summary>
[Collection("api")]
public sealed class LegalFolderSyncTests(IntegrationFixture fixture) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("plenipo-sync-test").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Bound_folder_syncs_into_matter_documents_and_knowledge_incrementally()
    {
        var contracts = Directory.CreateDirectory(Path.Combine(_root, "contracts")).FullName;
        await File.WriteAllTextAsync(Path.Combine(contracts, "supply-agreement.txt"),
            "Supply agreement with Contoso. Delivery within thirty days of purchase order acceptance.");
        await File.WriteAllTextAsync(Path.Combine(contracts, "warranty-terms.txt"),
            "Warranty terms. Defective units are replaced free of charge within twenty four months.");

        using var admin = fixture.ClientFor("system_admin");
        (await admin.PutAsJsonAsync("/api/admin/connectors/local-folder/settings",
            new { values = new Dictionary<string, string?> { ["RootPath"] = _root } })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/local-folder/enable", null)).EnsureSuccessStatusCode();

        try
        {
            using var scope = await UserScopeAsync();
            var matters = scope.ServiceProvider.GetRequiredService<MatterTools>();
            await matters.CreateMatter("Contoso supply");

            // Bind + first sync: both files attach and index.
            var bound = await matters.ConnectMatterFolder("Contoso supply", "contracts");
            Assert.Contains("Job id:", bound);
            await WaitForJobAsync(Guid.Parse(bound.Split("Job id:")[1].Split(' ')[1].Trim()));

            var listing = await matters.ListMatterDocuments("Contoso supply");
            Assert.Contains("supply-agreement.txt", listing);
            Assert.Contains("warranty-terms.txt", listing);

            var knowledge = scope.ServiceProvider.GetRequiredService<RagTools>();
            var answer = await knowledge.SearchKnowledge("warranty replacement period", collection: "matter: Contoso supply");
            Assert.Contains("warranty-terms.txt", answer);
            Assert.Contains("twenty four months", answer);

            // Incremental re-sync: one new file appears; the unchanged two are skipped, not duplicated.
            await File.WriteAllTextAsync(Path.Combine(contracts, "amendment-1.txt"),
                "Amendment one raises the delivery window to forty five days.");
            var resynced = await matters.SyncMatterFolder("Contoso supply");
            await WaitForJobAsync(Guid.Parse(resynced.Split("Job id:")[1].Split(' ')[1].Trim()));

            listing = await matters.ListMatterDocuments("Contoso supply");
            Assert.Contains("amendment-1.txt", listing);
            Assert.Equal(3, CountOccurrences(listing, "(file id:")); // 3 documents, no duplicates

            answer = await knowledge.SearchKnowledge("delivery window forty five days", collection: "matter: Contoso supply");
            Assert.Contains("amendment-1.txt", answer);
        }
        finally
        {
            (await admin.PostAsync("/api/admin/connectors/local-folder/disable", null)).EnsureSuccessStatusCode();
        }
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private async Task WaitForJobAsync(Guid jobId)
    {
        BackgroundJob? job = null;
        for (var i = 0; i < 120; i++)
        {
            using var pollScope = await UserScopeAsync();
            job = await pollScope.ServiceProvider.GetRequiredService<IJobQueue>().FindAsync(jobId);
            if (job?.Status is JobStatus.Succeeded or JobStatus.Failed)
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.True(job?.Status == JobStatus.Succeeded, $"sync job did not succeed: {job?.Status} — {job?.Error}");
    }

    /// <summary>A scope acting as the JIT-provisioned dev user with wildcard permissions.</summary>
    private async Task<IServiceScope> UserScopeAsync()
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
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == "it-user");
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
        return scope;
    }
}

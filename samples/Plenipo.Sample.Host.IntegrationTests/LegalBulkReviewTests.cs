using System.Text.Json;
using Plenipo.Application.Files;
using Plenipo.Application.Jobs;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Documents;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Modules.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// End-to-end coverage for the bulk review table (legal v1 item 7): start_bulk_review enqueues a
/// background job, the processor runs it under the enqueuer's captured authority, per-document
/// progress is recorded, every cell is an excerpt-grounded answer with a citation, and the finished
/// table is filed on the matter as a PDF. Keyless, real Postgres, real hosted processor.
/// </summary>
[Collection("api")]
public sealed class LegalBulkReviewTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Bulk_review_runs_as_a_job_and_files_the_table_on_the_matter()
    {
        using var scope = await DevUserScopeAsync();
        var files = scope.ServiceProvider.GetRequiredService<IFileStore>();
        var matters = scope.ServiceProvider.GetRequiredService<MatterTools>();

        await matters.CreateMatter("Vendor diligence", "Acme Corp");

        var docs = new (string Name, string Content)[]
        {
            ("vendor-a.txt", "MSA with Vendor A. Either party may terminate on ninety days notice. Liability is capped at fees paid."),
            ("vendor-b.txt", "Services agreement with Vendor B. Provider may terminate at any time without notice. No limitation of liability applies."),
        };
        foreach (var (name, content) in docs)
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            var stored = await files.SaveAsync(name, "text/plain", stream, source: "upload");
            await matters.AttachDocumentToMatter(stored.Id.ToString(), "Vendor diligence");
        }

        // Start the review through the tool (as the agent would, post-approval).
        var started = await matters.StartBulkReview(
            "vendor diligence", "What are the termination rights?; Is liability capped?");
        Assert.Contains("Job id:", started);
        var jobId = Guid.Parse(started.Split("Job id:")[1].Split(' ')[1].Trim());

        // The hosted processor picks it up; poll until terminal.
        BackgroundJob? job = null;
        for (var i = 0; i < 120; i++)
        {
            // Fresh context per poll so EF doesn't serve a stale tracked row.
            using var pollScope = await DevUserScopeAsync();
            job = await pollScope.ServiceProvider.GetRequiredService<IJobQueue>().FindAsync(jobId);
            if (job?.Status is JobStatus.Succeeded or JobStatus.Failed)
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.NotNull(job);
        Assert.True(job!.Status == JobStatus.Succeeded, $"job did not succeed: {job.Status} — {job.Error}");
        Assert.Equal(100, job.Progress);
        Assert.Contains("2/2 documents reviewed", job.ProgressNote);

        // The structured result: 2 rows × 2 questions, every answer grounded with a citation.
        var result = JsonSerializer.Deserialize<BulkReviewJobHandler.ReviewResult>(job.ResultJson!, JsonSerializerOptions.Web)!;
        Assert.Equal(2, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.Equal(2, r.Cells.Count));
        var vendorB = result.Rows.Single(r => r.FileName == "vendor-b.txt");
        Assert.Contains("terminate at any time", vendorB.Cells[0].Answer);
        Assert.Contains("file id:", vendorB.Cells[0].Answer);

        // The review table PDF is filed on the matter and readable through the tool surface.
        var listing = await matters.ListMatterDocuments("Vendor diligence");
        Assert.Contains("bulk-review-", listing);

        var documents = scope.ServiceProvider.GetRequiredService<DocumentTools>();
        var pdfText = await documents.ReadDocument(result.ReportFileId.ToString());
        Assert.Contains("termination rights", pdfText);
        Assert.Contains("vendor-b.txt", pdfText);
    }

    [Fact]
    public async Task Bulk_review_guides_the_agent_on_empty_input()
    {
        using var scope = await DevUserScopeAsync();
        var matters = scope.ServiceProvider.GetRequiredService<MatterTools>();

        Assert.Contains("No matter named", await matters.StartBulkReview("ghost", "anything?"));

        await matters.CreateMatter("Empty matter");
        Assert.Contains("no documents", await matters.StartBulkReview("Empty matter", "anything?"));
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

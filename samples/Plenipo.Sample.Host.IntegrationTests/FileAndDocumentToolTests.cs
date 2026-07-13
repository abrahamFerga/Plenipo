using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Plenipo.Application.Agents;
using Plenipo.Infrastructure.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// End-to-end coverage for the platform file store and the agent's document tools — all keyless:
/// PdfPig generates and reads PDFs in pure managed code, and files land on local disk. Covers the
/// upload/download endpoints (RBAC + tenant isolation) and the generate→read round-trip through the
/// same permission-filtered tool surface the agent uses.
/// </summary>
[Collection("api")]
public sealed class FileAndDocumentToolTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Upload_then_download_roundtrips_content()
    {
        using var client = fixture.ClientFor("user");
        var payload = "hello plenipo files"u8.ToArray();

        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(payload);
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", "hello.txt");

        var upload = await client.PostAsync("/api/files/", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var dto = await upload.Content.ReadFromJsonAsync<UploadedFile>();
        Assert.NotNull(dto);
        Assert.Equal("hello.txt", dto!.FileName);
        Assert.Equal(payload.Length, dto.SizeBytes);

        var download = await client.GetAsync($"/api/files/{dto.Id}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Guest_cannot_upload()
    {
        using var client = fixture.ClientFor("guest");
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("x"u8.ToArray()), "file", "x.txt" },
        };

        var response = await client.PostAsync("/api/files/", form);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Files_are_tenant_isolated()
    {
        await fixture.EnsureTenantAsync("tenant-b");

        using var uploader = fixture.ClientFor("user");
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("secret"u8.ToArray()), "file", "secret.txt" },
        };
        var dto = await (await uploader.PostAsync("/api/files/", form)).Content.ReadFromJsonAsync<UploadedFile>();

        using var otherTenant = fixture.ClientForTenant("tenant_admin", "tenant-b");
        var response = await otherTenant.GetAsync($"/api/files/{dto!.Id}");

        // A foreign tenant's file id is indistinguishable from a nonexistent one.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Generate_pdf_then_read_document_roundtrips_through_the_tool_surface()
    {
        // Ensure the "it-user" dev user exists (JIT provisioning happens on the first HTTP call).
        using (var warmup = fixture.ClientFor("user"))
        {
            (await warmup.GetAsync("/api/platform/me")).EnsureSuccessStatusCode();
        }

        // Resolve the same permission-filtered tool surface the agent gets for ANY module — the
        // platform document tools are appended to each module's own tools.
        using var scope = fixture.Factory.Services.CreateScope();
        await ActAsDevUserAsync(scope);

        var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
        var tools = registry.GetModuleTools("legal", scope.ServiceProvider);

        Assert.Contains(tools, t => t.Name == "read_document");
        Assert.Contains(tools, t => t.Name == "generate_pdf");
        Assert.Contains(tools, t => t.Name == "list_documents");
        Assert.DoesNotContain(tools, t => t.Name == "ocr_document"); // no OCR engine registered by default

        var docTools = scope.ServiceProvider.GetRequiredService<DocumentTools>();

        var generated = await docTools.GeneratePdf(
            "Case brief — Julia Assange",
            "This brief summarizes the filing.\n\nThe defendant requests dismissal on all counts.",
            cancellationToken: CancellationToken.None);
        Assert.Contains("File id:", generated);

        var fileId = generated.Split("File id:")[1].Split('.')[0].Trim();
        var text = await docTools.ReadDocument(fileId, CancellationToken.None);

        Assert.Contains("Julia Assange", text);
        Assert.Contains("dismissal", text);

        var listing = await docTools.ListDocuments(CancellationToken.None);
        Assert.Contains("Case brief", listing);
        Assert.Contains("generate_pdf", listing); // provenance is recorded
    }

    /// <summary>Populates the scoped RequestContext the way the HTTP pipeline would for the dev user.</summary>
    private static async Task ActAsDevUserAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Persistence.PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();

        var tenant = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(db.Tenants, t => t.Slug == "dev");
        context.SetTenant(tenant.Id);

        var user = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstAsync(db.Users, u => u.Subject == "it-user");
        context.SetUser(user.Id, user.Subject, user.DisplayName);
        context.SetPermissions(["*"]);
    }

    private sealed record UploadedFile(Guid Id, string FileName, string ContentType, long SizeBytes);
}

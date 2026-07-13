using System.Net.Http.Json;
using System.Text;
using Plenipo.Connectors.Documenso;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The Documenso e-signature connector (service-mode): tenant-admin-configured instance +
/// API token (secret, write-only), then the full loop — a stored document goes out for
/// signature, its status reports, and the signed copy files back into the tenant store.
/// Keyless via a fake signing client — settings/permission plumbing stays fully real.
/// </summary>
[Collection("api")]
public sealed class DocumensoConnectorTests : IDisposable
{
    private sealed class FakeDocumensoClient : IDocumensoClient
    {
        public DocumensoConnection? LastConnection { get; private set; }
        public SignatureRequest? LastRequest { get; private set; }
        public string Status { get; set; } = "PENDING";

        public Task<string> SendForSignatureAsync(
            DocumensoConnection connection, SignatureRequest request, CancellationToken cancellationToken = default)
        {
            LastConnection = connection;
            LastRequest = request;
            return Task.FromResult("doc-77");
        }

        public Task<SignatureStatus?> GetStatusAsync(
            DocumensoConnection connection, string documentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SignatureStatus?>(documentId == "doc-77"
                ? new SignatureStatus(documentId, Status, "client@example.test: " + Status)
                : null);

        public Task<SignedDocument?> DownloadSignedAsync(
            DocumensoConnection connection, string documentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SignedDocument?>(documentId == "doc-77" && Status == "COMPLETED"
                ? new SignedDocument(new MemoryStream("signed!"u8.ToArray()), $"signed-{documentId}.pdf")
                : null);
    }

    private readonly FakeDocumensoClient _documenso = new();
    private readonly WebApplicationFactory<Program> _factory;

    public DocumensoConnectorTests(IntegrationFixture fixture)
    {
        _factory = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
                services.AddSingleton<IDocumensoClient>(_documenso)));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Stored_document_goes_out_signs_and_files_back()
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Dev-Subject", "it-system_admin");
        admin.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        admin.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");

        (await admin.PutAsJsonAsync("/api/admin/connectors/documenso/settings", new
        {
            values = new Dictionary<string, string?>
            {
                ["BaseUrl"] = "https://sign.firm.test",
                ["ApiToken"] = "api_fake_token",
            },
        })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/documenso/enable", null)).EnsureSuccessStatusCode();

        try
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();
            var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
            context.SetTenant(tenant.Id);
            var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.TenantId == tenant.Id);
            context.SetUser(user.Id, user.Subject, user.DisplayName);
            context.SetPermissions(["*"]);

            // A document the firm generated — the thing that needs signing.
            var files = scope.ServiceProvider.GetRequiredService<Plenipo.Application.Files.IFileStore>();
            using var pdf = new MemoryStream(Encoding.UTF8.GetBytes("engagement letter"));
            var stored = await files.SaveAsync("engagement-letter.pdf", "application/pdf", pdf, source: "test");

            var tools = scope.ServiceProvider.GetRequiredService<DocumensoTools>();

            // Out for signature — on the TENANT's configured instance, with the file's real bytes.
            var sent = await tools.SendForSignature(stored.Id.ToString(), "client@example.test", "Ada Client");
            Assert.Contains("doc-77", sent);
            Assert.Equal("https://sign.firm.test", _documenso.LastConnection!.BaseUrl);
            Assert.Equal("api_fake_token", _documenso.LastConnection.ApiToken);
            Assert.Equal("engagement letter", Encoding.UTF8.GetString(_documenso.LastRequest!.Content));

            // Pending → completed status reads.
            Assert.Contains("PENDING", await tools.CheckSignatureStatus("doc-77"));
            Assert.Contains("no completed document yet", await tools.FetchSignedDocument("doc-77"));

            _documenso.Status = "COMPLETED";
            Assert.Contains("COMPLETED", await tools.CheckSignatureStatus("doc-77"));

            // The signed copy lands in the tenant file store, source-stamped.
            var fetched = await tools.FetchSignedDocument("doc-77");
            Assert.Contains("Filed signed document", fetched);
            Assert.Contains("File id:", fetched);

            // Unknown ids answer readably.
            Assert.Contains("No signature request", await tools.CheckSignatureStatus("doc-404"));
        }
        finally
        {
            await admin.PostAsync("/api/admin/connectors/documenso/disable", null);
        }
    }

    [Fact]
    public async Task Unconfigured_connector_answers_with_guidance()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        context.SetPermissions(["*"]);

        var tools = scope.ServiceProvider.GetRequiredService<DocumensoTools>();
        Assert.Contains("not enabled for this tenant", await tools.CheckSignatureStatus("doc-1"));
    }
}

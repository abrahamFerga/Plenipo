using System.Net.Http.Json;
using Plenipo.Connectors.S3;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The S3 connector (service-mode, like Azure Blob): tenant-admin-configured credentials
/// (secret, write-only), browse + approval-gated import into the tenant file store. Keyless via
/// a fake S3 client — the settings/permission plumbing stays fully real.
/// </summary>
[Collection("api")]
public sealed class S3ConnectorTests : IDisposable
{
    private sealed class FakeS3Client : IS3ObjectClient
    {
        public S3Connection? LastConnection { get; private set; }

        public Task<IReadOnlyList<S3Entry>> ListAsync(
            S3Connection connection, string? prefix, CancellationToken cancellationToken = default)
        {
            LastConnection = connection;
            IReadOnlyList<S3Entry> all = [new("contracts/msa.txt", 42), new("notes/todo.txt", 7)];
            return Task.FromResult<IReadOnlyList<S3Entry>>(
                [.. all.Where(o => prefix is null || o.Key.StartsWith(prefix, StringComparison.Ordinal))]);
        }

        public Task<S3Content?> DownloadAsync(
            S3Connection connection, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(key == "contracts/msa.txt"
                ? new S3Content(new MemoryStream("Master services agreement."u8.ToArray()), "text/plain")
                : null);
    }

    private readonly FakeS3Client _s3 = new();
    private readonly WebApplicationFactory<Program> _factory;

    public S3ConnectorTests(IntegrationFixture fixture)
    {
        _factory = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IS3ObjectClient>(_s3))));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ConfiguredBucket_ListsAndImports_OnTheTenantsSettings()
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Dev-Subject", "it-system_admin");
        admin.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        admin.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");

        (await admin.PutAsJsonAsync("/api/admin/connectors/s3/settings", new
        {
            values = new Dictionary<string, string?>
            {
                ["AccessKeyId"] = "AKIA_FAKE",
                ["SecretAccessKey"] = "fake-secret",
                ["Bucket"] = "firm-docs",
                ["ServiceUrl"] = "http://minio.local:9000",
            },
        })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/admin/connectors/s3/enable", null)).EnsureSuccessStatusCode();

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

            var tools = scope.ServiceProvider.GetRequiredService<S3Tools>();

            // List rides the TENANT's protected settings (bucket + S3-compatible endpoint intact).
            var listing = await tools.ListS3Objects("contracts/");
            Assert.Contains("contracts/msa.txt", listing);
            Assert.DoesNotContain("notes/todo.txt", listing);
            Assert.Equal("firm-docs", _s3.LastConnection!.Bucket);
            Assert.Equal("http://minio.local:9000", _s3.LastConnection.ServiceUrl);
            Assert.Equal("us-east-1", _s3.LastConnection.Region); // the default applied

            // Import lands in the tenant file store with the connector source stamp.
            var fetched = await tools.FetchFromS3("contracts/msa.txt");
            Assert.Contains("Imported 'msa.txt'", fetched);
            Assert.Contains("File id:", fetched);

            // Missing objects answer readably.
            Assert.Contains("No object with key", await tools.FetchFromS3("contracts/nope.txt"));
        }
        finally
        {
            await admin.PostAsync("/api/admin/connectors/s3/disable", null);
        }
    }

    [Fact]
    public async Task UnconfiguredConnector_AnswersWithGuidance()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var context = scope.ServiceProvider.GetRequiredService<Plenipo.Infrastructure.Context.RequestContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        context.SetTenant(tenant.Id);
        context.SetPermissions(["*"]);

        var tools = scope.ServiceProvider.GetRequiredService<S3Tools>();
        Assert.Contains("not enabled for this tenant", await tools.ListS3Objects());
    }
}

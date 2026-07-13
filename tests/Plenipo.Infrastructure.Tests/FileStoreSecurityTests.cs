using Plenipo.Application.Files;
using Plenipo.Core.Identity;
using Plenipo.Core.Multitenancy;
using Plenipo.Infrastructure.Files;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Tests;

public sealed class FileStoreSecurityTests
{
    [Fact]
    public async Task SaveAsync_EnforcesTheGlobalStorageLimitBeforeWritingTheBlob()
    {
        var identity = new TestIdentity();
        var dbOptions = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase($"file-limit-{Guid.NewGuid():N}")
            .Options;
        await using var db = new PlatformDbContext(dbOptions, identity);
        var blobs = new RecordingBlobStorage();
        var store = new FileStore(
            db,
            blobs,
            identity,
            Options.Create(new FileStorageOptions { MaxUploadBytes = 4 }));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(
            "oversized.bin",
            "application/octet-stream",
            new MemoryStream(new byte[5]),
            "test"));

        Assert.Equal(0, blobs.WriteCount);
        Assert.Empty(db.StoredFiles);
    }

    private sealed class TestIdentity : ICurrentUser, ITenantContext
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public Guid? TenantId { get; } = Guid.NewGuid();
        public string? Subject => "test-user";
        public string? DisplayName => "Test User";
        public bool IsAuthenticated => true;
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
        public bool HasTenant => true;
        public bool HasPermission(string permission) => false;
        public Guid RequireTenantId() => TenantId!.Value;
    }

    private sealed class RecordingBlobStorage : IFileBlobStorage
    {
        public int WriteCount { get; private set; }

        public Task WriteAsync(
            Guid tenantId,
            Guid fileId,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task<Stream?> OpenReadAsync(
            Guid tenantId,
            Guid fileId,
            CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
    }
}

using System.Security.Cryptography;
using Cortex.Application.Files;
using Cortex.Core.Identity;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cortex.Infrastructure.Files;

/// <summary>
/// EF-backed <see cref="IFileStore"/>: metadata rows in the platform database (tenant query filter
/// applies to every read) with content delegated to the configured <see cref="IFileBlobStorage"/>.
/// </summary>
public sealed class FileStore(
    PlatformDbContext db,
    IFileBlobStorage blobs,
    ICurrentUser currentUser,
    IOptions<FileStorageOptions> options) : IFileStore
{
    public async Task<StoredFile> SaveAsync(
        string fileName, string contentType, Stream content, string source, CancellationToken cancellationToken = default)
    {
        var tenantId = currentUser.TenantId
            ?? throw new InvalidOperationException("Cannot store a file without a tenant.");
        var userId = currentUser.UserId
            ?? throw new InvalidOperationException("Cannot store a file without a user.");

        // Buffer once to hash and measure. Enforce the limit here—not only at the HTTP endpoint—
        // because channel and connector callers also ingest untrusted content.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > options.Value.MaxUploadBytes)
            {
                throw new InvalidDataException(
                    $"The file exceeds the {options.Value.MaxUploadBytes / (1024 * 1024)} MB storage limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        buffer.Position = 0;

        var file = new StoredFile
        {
            TenantId = tenantId,
            UserId = userId,
            FileName = Path.GetFileName(fileName), // never trust a client path
            ContentType = contentType,
            SizeBytes = buffer.Length,
            Sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(buffer, cancellationToken)),
            Source = source,
        };
        buffer.Position = 0;

        await blobs.WriteAsync(tenantId, file.Id, buffer, cancellationToken);

        db.StoredFiles.Add(file);
        await db.SaveChangesAsync(cancellationToken);
        return file;
    }

    public async Task<StoredFile?> FindAsync(Guid fileId, CancellationToken cancellationToken = default) =>
        await db.StoredFiles.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

    public async Task<Stream?> OpenReadAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        // Metadata lookup first: it carries the tenant filter, so a foreign tenant's id resolves to
        // null here and the blob layer is never consulted.
        var file = await FindAsync(fileId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        return await blobs.OpenReadAsync(file.TenantId, file.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredFile>> ListMineAsync(int take = 20, CancellationToken cancellationToken = default) =>
        await db.StoredFiles
            .Where(f => f.UserId == currentUser.UserId)
            .OrderByDescending(f => f.CreatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);
}

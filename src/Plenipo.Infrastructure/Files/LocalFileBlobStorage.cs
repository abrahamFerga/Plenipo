using Plenipo.Application.Files;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Files;

/// <summary>
/// Local-disk <see cref="IFileBlobStorage"/> — the zero-setup dev default. Content lands under
/// <c>{LocalRoot}/{tenantId}/{fileId}</c>; ids are GUIDs the platform generates, never user input.
/// </summary>
public sealed class LocalFileBlobStorage(IOptions<FileStorageOptions> options, IHostEnvironment environment) : IFileBlobStorage
{
    private readonly string _root = Path.IsPathRooted(options.Value.LocalRoot)
        ? options.Value.LocalRoot
        : Path.Combine(environment.ContentRootPath, options.Value.LocalRoot);

    public async Task WriteAsync(Guid tenantId, Guid fileId, Stream content, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_root, tenantId.ToString("N"));
        Directory.CreateDirectory(directory);

        await using var target = File.Create(Path.Combine(directory, fileId.ToString("N")));
        await content.CopyToAsync(target, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, tenantId.ToString("N"), fileId.ToString("N"));
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }
}

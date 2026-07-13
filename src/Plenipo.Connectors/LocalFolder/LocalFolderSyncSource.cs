using Plenipo.Connectors.Sdk;
using Microsoft.Extensions.Options;

namespace Plenipo.Connectors.LocalFolder;

/// <summary>
/// The local-folder connector's sync lane: a binding's external ref is a subfolder (or ".") under
/// the admin-configured root, containment-checked like the tools. The content stamp is
/// length + last-write ticks — cheap and enough for a dev/watched-folder source.
/// </summary>
public sealed class LocalFolderSyncSource(
    IConnectorSettings settings,
    IOptions<LocalFolderOptions> options) : IConnectorSyncSource
{
    public string ConnectorId => LocalFolderConnector.ConnectorId;

    public async Task<IReadOnlyList<ConnectorSyncFile>?> ListAsync(
        string externalRef, CancellationToken cancellationToken = default)
    {
        var folder = await ResolveAsync(externalRef, cancellationToken);
        if (folder is null)
        {
            return null;
        }

        return new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            })
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .Select(f => new ConnectorSyncFile(
                Path.GetRelativePath(folder, f.FullName).Replace('\\', '/'),
                f.Name,
                LocalFolderTools.ContentTypeFor(f.FullName),
                $"{f.Length}:{f.LastWriteTimeUtc.Ticks}"))
            .ToList();
    }

    public async Task<Stream?> OpenAsync(string externalRef, string fileId, CancellationToken cancellationToken = default)
    {
        var folder = await ResolveAsync(externalRef, cancellationToken);
        if (folder is null)
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(folder, fileId));
        return LocalFolderPathPolicy.IsContained(folder, full) &&
               !LocalFolderPathPolicy.ContainsReparsePoint(folder, full) && File.Exists(full)
            ? File.OpenRead(full)
            : null;
    }

    /// <summary>Resolves ref → absolute folder, containment-checked against the configured root.</summary>
    private async Task<string?> ResolveAsync(string externalRef, CancellationToken cancellationToken)
    {
        var values = await settings.GetAsync(LocalFolderConnector.ConnectorId, cancellationToken);
        if (values is null ||
            !values.TryGetValue(LocalFolderConnector.RootPathSetting, out var root) ||
            string.IsNullOrWhiteSpace(root) ||
            !Directory.Exists(root))
        {
            return null;
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!LocalFolderPathPolicy.IsAllowedRoot(root, options.Value.AllowedRoots))
        {
            return null;
        }
        var folder = Path.GetFullPath(Path.Combine(root, externalRef));
        var contained = folder.Equals(
                            root,
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                        LocalFolderPathPolicy.IsContained(root, folder);
        return contained && !LocalFolderPathPolicy.ContainsReparsePoint(root, folder) && Directory.Exists(folder)
            ? folder
            : null;
    }
}

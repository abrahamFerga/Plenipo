using System.ComponentModel;
using System.Text;
using Plenipo.Application.Authorization;
using Plenipo.Application.Files;
using Plenipo.Connectors.Sdk;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Plenipo.Connectors.LocalFolder;

/// <summary>
/// The local-folder connector's tools. Every path resolves against — and is verified to stay
/// under — the admin-configured root; a traversal attempt reads as "no such file". Fetch copies
/// into the tenant file store, which is what makes everything downstream (attachments,
/// read_document, matters, RAG ingestion) work on connector files with no special casing.
/// </summary>
public sealed class LocalFolderTools(
    IConnectorSettings settings,
    IFileStore files,
    IOptions<LocalFolderOptions> options)
{
    private const string NotConfigured =
        "The local-folder connector is not enabled for this tenant (or has no root path configured). " +
        "An admin can enable it under Integrations.";

    [Description("List the files available in the connected local folder (name and size). Use fetch_from_local_folder to import one.")]
    public async Task<string> ListLocalFolder(CancellationToken cancellationToken = default)
    {
        var root = await ResolveRootAsync(cancellationToken);
        if (root is null)
        {
            return NotConfigured;
        }

        var entries = new DirectoryInfo(root)
            .EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
            })
            .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();
        if (entries.Count == 0)
        {
            return "The connected folder is empty.";
        }

        var sb = new StringBuilder("Files in the connected folder:\n");
        foreach (var file in entries)
        {
            var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            sb.AppendLine($"- {relative} ({file.Length:N0} bytes)");
        }

        return sb.ToString();
    }

    [Description("Copy a file from the connected local folder into the tenant file store and return its file id (usable with read_document, attach tools, and indexing).")]
    public async Task<string> FetchFromLocalFolder(
        [Description("The file's path relative to the connected folder, as shown by list_local_folder.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        var root = await ResolveRootAsync(cancellationToken);
        if (root is null)
        {
            return NotConfigured;
        }

        // Containment check: the resolved path must stay under the configured root — a traversal
        // attempt is indistinguishable from a missing file.
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (!LocalFolderPathPolicy.IsContained(root, full) ||
            LocalFolderPathPolicy.ContainsReparsePoint(root, full) ||
            !File.Exists(full))
        {
            return $"No file named '{path}' exists in the connected folder. Use list_local_folder to see what is available.";
        }

        await using var stream = File.OpenRead(full);
        var stored = await files.SaveAsync(
            Path.GetFileName(full), ContentTypeFor(full), stream,
            source: $"connector:{LocalFolderConnector.ConnectorId}", cancellationToken);

        return $"Imported '{stored.FileName}' ({stored.SizeBytes:N0} bytes) from the local folder. File id: {stored.Id}. Download: /api/files/{stored.Id}";
    }

    private async Task<string?> ResolveRootAsync(CancellationToken cancellationToken)
    {
        var values = await settings.GetAsync(LocalFolderConnector.ConnectorId, cancellationToken);
        if (values is null ||
            !values.TryGetValue(LocalFolderConnector.RootPathSetting, out var root) ||
            string.IsNullOrWhiteSpace(root) ||
            !Directory.Exists(root))
        {
            return null;
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return LocalFolderPathPolicy.IsAllowedRoot(normalized, options.Value.AllowedRoots)
            ? normalized
            : null;
    }

    internal static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".txt" or ".md" or ".log" => "text/plain",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".csv" => "text/csv",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };
}

internal static class LocalFolderPathPolicy
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsAllowedRoot(string candidate, IEnumerable<string> allowedRoots)
    {
        foreach (var configured in allowedRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var allowed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            if (candidate.Equals(allowed, PathComparison) || IsContained(allowed, candidate))
            {
                return !ContainsReparsePoint(allowed, candidate);
            }
        }

        return false;
    }

    public static bool IsContained(string root, string candidate) =>
        candidate.StartsWith(
            Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            PathComparison);

    public static bool ContainsReparsePoint(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative.StartsWith("..", StringComparison.Ordinal))
        {
            return true;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Supplies the local-folder connector's executable tools.</summary>
public sealed class LocalFolderToolSource : IConnectorToolSource
{
    public string ConnectorId => LocalFolderConnector.ConnectorId;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<LocalFolderTools>();
        return
        [
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "list_local_folder",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_local_folder"),
                Function = AIFunctionFactory.Create(tools.ListLocalFolder, name: "list_local_folder"),
            },
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "fetch_from_local_folder",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_from_local_folder"),
                Function = AIFunctionFactory.Create(tools.FetchFromLocalFolder, name: "fetch_from_local_folder"),
                RequiresApproval = true,
            },
        ];
    }
}

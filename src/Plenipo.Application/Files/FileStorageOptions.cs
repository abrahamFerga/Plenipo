namespace Plenipo.Application.Files;

/// <summary>
/// File-store configuration, bound from the "Files" section. The Local provider needs no setup
/// (files under <see cref="LocalRoot"/>); production points at Azure Blob Storage.
/// </summary>
public sealed class FileStorageOptions
{
    public const string SectionName = "Files";

    /// <summary>"Local" (default) or "AzureBlob".</summary>
    public string Provider { get; set; } = "Local";

    /// <summary>Root directory for the Local provider. Relative paths resolve under the content root.</summary>
    public string LocalRoot { get; set; } = "data/files";

    /// <summary>Azure Blob connection string (secret — Key Vault / user-secrets) for the AzureBlob provider.</summary>
    public string? AzureBlobConnectionString { get; set; }

    /// <summary>Container name for the AzureBlob provider.</summary>
    public string AzureBlobContainer { get; set; } = "plenipo-files";

    /// <summary>Upload size cap in bytes (default 20 MB).</summary>
    public long MaxUploadBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Throws when the configured provider is missing a setting it cannot run without.</summary>
    public void ThrowIfInvalid()
    {
        if (string.Equals(Provider, "AzureBlob", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(AzureBlobConnectionString))
        {
            throw new InvalidOperationException(
                "Files:Provider is AzureBlob but Files:AzureBlobConnectionString is not set " +
                "(use Key Vault / user-secrets). Or set Files:Provider=Local.");
        }
    }
}

namespace Plenipo.Infrastructure.Secrets;

/// <summary>
/// Selects the secret backend. The default keeps today's behavior — DataProtection ciphertext
/// inline in the database. <c>AzureKeyVault</c> moves the VALUES to Key Vault while the database
/// keeps only <c>kv:</c> references; the admin UI is unchanged either way.
/// </summary>
public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";

    public const string DataProtectionProvider = "DataProtection";
    public const string AzureKeyVaultProvider = "AzureKeyVault";

    public string Provider { get; set; } = DataProtectionProvider;

    /// <summary>Required when <see cref="Provider"/> is AzureKeyVault, e.g. https://myvault.vault.azure.net/.</summary>
    public string? KeyVaultUri { get; set; }

    /// <summary>Prefix for generated Key Vault secret names (one vault can serve several deployments).</summary>
    public string KeyVaultSecretPrefix { get; set; } = "plenipo";
}

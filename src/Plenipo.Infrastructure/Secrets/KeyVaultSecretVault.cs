using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Plenipo.Application.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Secrets;

/// <summary>
/// Key Vault backend: values live as Key Vault secrets under generated names; the database keeps
/// only <c>kv:{name}</c> pointers. References written by the DataProtection backend (or legacy
/// bare ciphertext) still resolve through the inner vault, so flipping <c>Secrets:Provider</c> on
/// an existing deployment is safe — old secrets keep working, new writes land in Key Vault.
/// Auth is <see cref="DefaultAzureCredential"/> (managed identity in Azure, developer sign-in
/// locally); grant the identity get/set/delete secret permissions.
/// </summary>
public sealed class KeyVaultSecretVault(
    IOptions<SecretsOptions> options,
    DataProtectionSecretVault inner,
    ILogger<KeyVaultSecretVault> logger) : ISecretVault
{
    internal const string Prefix = "kv:";

    private readonly Lazy<SecretClient> _client = new(() =>
    {
        var uri = options.Value.KeyVaultUri;
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new InvalidOperationException(
                "Secrets:Provider is AzureKeyVault but Secrets:KeyVaultUri is not configured.");
        }

        return new SecretClient(new Uri(uri), new DefaultAzureCredential());
    });

    public async Task<string> StoreAsync(string scope, string value, CancellationToken cancellationToken = default)
    {
        // A random name (not derived from the value or tenant) — the pointer row is the only map.
        var name = $"{options.Value.KeyVaultSecretPrefix}-{Guid.NewGuid():N}";
        await _client.Value.SetSecretAsync(new KeyVaultSecret(name, value), cancellationToken);
        return Prefix + name;
    }

    public async Task<string> RevealAsync(string scope, string storedReference, CancellationToken cancellationToken = default)
    {
        if (!storedReference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // Written before the switch to Key Vault — the DataProtection vault owns it.
            return await inner.RevealAsync(scope, storedReference, cancellationToken);
        }

        var secret = await _client.Value.GetSecretAsync(storedReference[Prefix.Length..], cancellationToken: cancellationToken);
        return secret.Value.Value;
    }

    public async Task ForgetAsync(string storedReference, CancellationToken cancellationToken = default)
    {
        if (!storedReference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await _client.Value.StartDeleteSecretAsync(storedReference[Prefix.Length..], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cleanup is best-effort: a dangling Key Vault secret is unreferenced noise, not a leak.
            logger.LogWarning(ex, "Failed to delete Key Vault secret for a removed reference");
        }
    }
}

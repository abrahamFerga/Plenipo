using System.Text.Json;
using Plenipo.Application.Connectors;
using Plenipo.Application.Secrets;
using Plenipo.Connectors.Sdk;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Connectors;

/// <summary>
/// Reads and writes a tenant's connector settings. Values for manifest-declared secret fields go
/// through the configured <see cref="ISecretVault"/> (DataProtection ciphertext by default, Azure
/// Key Vault when <c>Secrets:Provider</c> says so) and come back to plaintext only here, on the
/// server, for connector code — the admin API never echoes a secret back (it reports "a value
/// exists"), which is what lets non-technical admins manage keys safely from the UI.
/// </summary>
public sealed class ConnectorSettingsService(
    PlatformDbContext db,
    IConnectorCatalog catalog,
    ISecretVault vault) : IConnectorSettings
{
    private const string SecretScope = "Plenipo.Connectors.Settings";

    public async Task<IReadOnlyDictionary<string, string>?> GetAsync(
        string connectorId, CancellationToken cancellationToken = default)
    {
        var row = await db.TenantConnectors
            .FirstOrDefaultAsync(c => c.ConnectorId == connectorId && c.Enabled, cancellationToken);
        if (row is null)
        {
            return null; // not enabled for this tenant — callers answer honestly, never guess
        }

        if (!catalog.TryGetManifest(connectorId, out var manifest) || manifest is null)
        {
            return null; // enabled row for a connector this host no longer installs
        }

        var stored = Deserialize(row.SettingsJson);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in stored)
        {
            var descriptor = manifest.Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));
            result[key] = descriptor is { IsSecret: true }
                ? await vault.RevealAsync(SecretScope, value, cancellationToken)
                : value;
        }

        return result;
    }

    /// <summary>
    /// Merges admin-submitted values into the stored settings: secrets go to the vault on the way
    /// in, and an omitted secret keeps its existing value (the admin UI can't echo it back to
    /// resubmit). Replaced or cleared secrets are forgotten from the vault best-effort.
    /// </summary>
    public async Task SaveAsync(
        TenantConnector row, ConnectorManifest manifest, IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken = default)
    {
        var stored = Deserialize(row.SettingsJson);

        foreach (var descriptor in manifest.Settings)
        {
            if (!values.TryGetValue(descriptor.Key, out var value) || value is null)
            {
                continue; // untouched field keeps its stored value
            }

            var previous = descriptor.IsSecret && stored.TryGetValue(descriptor.Key, out var p) ? p : null;

            if (string.IsNullOrEmpty(value))
            {
                stored.Remove(descriptor.Key);
            }
            else
            {
                stored[descriptor.Key] = descriptor.IsSecret
                    ? await vault.StoreAsync(SecretScope, value, cancellationToken)
                    : value;
            }

            if (previous is not null)
            {
                await vault.ForgetAsync(previous, cancellationToken);
            }
        }

        row.SettingsJson = JsonSerializer.Serialize(stored);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Which settings currently have a value — what the admin API may reveal about secrets.</summary>
    public IReadOnlySet<string> KeysWithValues(TenantConnector? row) =>
        row is null ? new HashSet<string>() : Deserialize(row.SettingsJson).Keys.ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
}

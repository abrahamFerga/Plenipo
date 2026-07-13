namespace Plenipo.Application.Secrets;

/// <summary>
/// Where secret values physically live. Callers persist only the OPAQUE REFERENCE this vault
/// returns (ciphertext for the DataProtection backend, a <c>kv:</c> pointer for Key Vault) and
/// get the plaintext back only server-side via <see cref="RevealAsync"/> — so the admin UI's
/// write-only contract holds regardless of backend, and switching backends is configuration
/// (<c>Secrets:Provider</c>), not a data migration: references self-describe via prefix.
/// </summary>
public interface ISecretVault
{
    /// <summary>Stores <paramref name="value"/> and returns the reference to persist. <paramref name="scope"/> partitions cryptographic purposes (e.g. connector settings vs user tokens).</summary>
    public Task<string> StoreAsync(string scope, string value, CancellationToken cancellationToken = default);

    /// <summary>Resolves a stored reference back to plaintext. Must accept references written by any prior backend.</summary>
    public Task<string> RevealAsync(string scope, string storedReference, CancellationToken cancellationToken = default);

    /// <summary>Best-effort cleanup when a secret is removed or replaced (deletes the Key Vault secret; no-op for inline ciphertext).</summary>
    public Task ForgetAsync(string storedReference, CancellationToken cancellationToken = default);
}

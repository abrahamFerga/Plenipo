using Plenipo.Application.Secrets;
using Microsoft.AspNetCore.DataProtection;

namespace Plenipo.Infrastructure.Secrets;

/// <summary>
/// The default backend: the value IS the reference — DataProtection ciphertext stored inline by
/// the caller. References carry a <c>dp:</c> prefix; prefixless values are accepted as legacy
/// ciphertext from before the vault seam existed, so no migration is needed.
/// </summary>
public sealed class DataProtectionSecretVault(IDataProtectionProvider dataProtection) : ISecretVault
{
    internal const string Prefix = "dp:";

    public Task<string> StoreAsync(string scope, string value, CancellationToken cancellationToken = default) =>
        Task.FromResult(Prefix + dataProtection.CreateProtector(scope).Protect(value));

    public Task<string> RevealAsync(string scope, string storedReference, CancellationToken cancellationToken = default)
    {
        var payload = storedReference.StartsWith(Prefix, StringComparison.Ordinal)
            ? storedReference[Prefix.Length..]
            : storedReference; // legacy: pre-seam rows stored bare ciphertext
        return Task.FromResult(dataProtection.CreateProtector(scope).Unprotect(payload));
    }

    public Task ForgetAsync(string storedReference, CancellationToken cancellationToken = default) =>
        Task.CompletedTask; // inline ciphertext dies with the row
}

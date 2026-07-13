using Plenipo.Infrastructure.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Tests;

public sealed class SecretVaultTests
{
    private static DataProtectionSecretVault NewDpVault() =>
        new(new EphemeralDataProtectionProvider());

    [Fact]
    public async Task DataProtection_RoundTrips_WithDpPrefix()
    {
        var vault = NewDpVault();

        var reference = await vault.StoreAsync("scope-a", "s3cret");

        Assert.StartsWith("dp:", reference, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", reference, StringComparison.Ordinal);
        Assert.Equal("s3cret", await vault.RevealAsync("scope-a", reference));
    }

    [Fact]
    public async Task DataProtection_AcceptsLegacyBareCiphertext()
    {
        // Rows written before the vault seam stored protector output with no prefix.
        var provider = new EphemeralDataProtectionProvider();
        var legacy = provider.CreateProtector("scope-a").Protect("old-secret");
        var vault = new DataProtectionSecretVault(provider);

        Assert.Equal("old-secret", await vault.RevealAsync("scope-a", legacy));
    }

    [Fact]
    public async Task DataProtection_ScopesAreCryptographicallyIsolated()
    {
        var vault = NewDpVault();
        var reference = await vault.StoreAsync("scope-a", "s3cret");

        await Assert.ThrowsAnyAsync<Exception>(() => vault.RevealAsync("scope-b", reference));
    }

    [Fact]
    public async Task KeyVault_DelegatesNonKvReferences_ToTheInnerVault_WithoutTouchingKeyVault()
    {
        // No KeyVaultUri configured: any Key Vault access would throw. Non-kv references must be
        // answered by the inner DataProtection vault alone — this is what makes flipping
        // Secrets:Provider on an existing deployment safe.
        var inner = NewDpVault();
        var kv = new KeyVaultSecretVault(
            Options.Create(new SecretsOptions { Provider = SecretsOptions.AzureKeyVaultProvider }),
            inner,
            NullLogger<KeyVaultSecretVault>.Instance);

        var dpReference = await inner.StoreAsync("scope-a", "s3cret");

        Assert.Equal("s3cret", await kv.RevealAsync("scope-a", dpReference));
        await kv.ForgetAsync(dpReference); // non-kv: no-op, must not throw
    }

    [Fact]
    public async Task KeyVault_StoreWithoutUri_FailsFast()
    {
        var kv = new KeyVaultSecretVault(
            Options.Create(new SecretsOptions { Provider = SecretsOptions.AzureKeyVaultProvider }),
            NewDpVault(),
            NullLogger<KeyVaultSecretVault>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => kv.StoreAsync("scope-a", "value"));
    }
}

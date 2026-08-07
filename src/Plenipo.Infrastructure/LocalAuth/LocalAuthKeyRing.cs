using System.Security.Cryptography;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Plenipo.Infrastructure.LocalAuth;

/// <summary>
/// The embedded issuer's key material (ADR 0003): one RSA signing key (access tokens the platform's
/// own JwtBearer validation trusts) and one 256-bit symmetric key (OpenIddict wraps authorization
/// codes and refresh tokens with it). Loaded — or generated, first boot — from the platform database
/// by <c>DatabaseInitializer</c> BEFORE the host serves traffic, then immutable for the process
/// lifetime. Stored Data-Protection-protected, and exposed as raw crypto primitives so the
/// infrastructure layer stays free of Microsoft.IdentityModel types.
/// </summary>
public interface ILocalAuthKeyRing
{
    /// <summary>Stable key id (the row's id) — becomes the JWKS <c>kid</c>.</summary>
    public string SigningKeyId { get; }

    /// <summary>The RSA signing key pair. Callers must not dispose it.</summary>
    public RSA SigningKey { get; }

    /// <summary>The 256-bit symmetric encryption key.</summary>
    public byte[] EncryptionKey { get; }

    /// <summary>Loads or creates the keys. Idempotent; safe under a multi-instance first-boot race.</summary>
    public Task EnsureInitializedAsync(PlatformDbContext db, CancellationToken cancellationToken = default);
}

public sealed class LocalAuthKeyRing(
    IDataProtectionProvider dataProtection,
    ILogger<LocalAuthKeyRing> logger) : ILocalAuthKeyRing
{
    private const string ProtectorPurpose = "Plenipo.LocalAuth.Keys";
    private const string SigningUse = "sig";
    private const string EncryptionUse = "enc";

    private RSA? signingKey;
    private string? signingKeyId;
    private byte[]? encryptionKey;

    public string SigningKeyId => signingKeyId ?? throw NotInitialized();
    public RSA SigningKey => signingKey ?? throw NotInitialized();
    public byte[] EncryptionKey => encryptionKey ?? throw NotInitialized();

    public async Task EnsureInitializedAsync(PlatformDbContext db, CancellationToken cancellationToken = default)
    {
        if (signingKey is not null)
        {
            return;
        }

        var protector = dataProtection.CreateProtector(ProtectorPurpose);

        var signingRow = await LoadOrCreateAsync(db, SigningUse, () =>
        {
            using var rsa = RSA.Create(2048);
            return protector.Protect(rsa.ExportPkcs8PrivateKey());
        }, cancellationToken);
        var encryptionRow = await LoadOrCreateAsync(db, EncryptionUse,
            () => protector.Protect(RandomNumberGenerator.GetBytes(32)), cancellationToken);

        // Both the fresh-create and the load path import from the stored form — one code path, and a
        // first boot proves the round-trip it will depend on at every later boot.
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(protector.Unprotect(Convert.FromBase64String(signingRow.ProtectedKey)), out _);

        signingKeyId = signingRow.Id.ToString("N");
        encryptionKey = protector.Unprotect(Convert.FromBase64String(encryptionRow.ProtectedKey));
        signingKey = rsa; // assigned last: it is the initialization sentinel the properties check
    }

    private async Task<LocalAuthKey> LoadOrCreateAsync(
        PlatformDbContext db, string use, Func<byte[]> createProtected, CancellationToken cancellationToken)
    {
        var existing = await db.LocalAuthKeys.FirstOrDefaultAsync(k => k.Use == use, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var row = new LocalAuthKey { Use = use, ProtectedKey = Convert.ToBase64String(createProtected()) };
        db.LocalAuthKeys.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Local auth: generated the deployment's {Use} key ({KeyId}).", use, row.Id);
            }

            return row;
        }
        catch (DbUpdateException)
        {
            // Two instances raced the first boot; the unique index on Use arbitrated. Use the winner's —
            // both instances MUST hold the same keys or half the fleet's tokens fail validation.
            db.Entry(row).State = EntityState.Detached;
            return await db.LocalAuthKeys.FirstAsync(k => k.Use == use, cancellationToken);
        }
    }

    private static InvalidOperationException NotInitialized() => new(
        "The local auth key ring has not been initialized. DatabaseInitializer must run before the host serves traffic.");
}

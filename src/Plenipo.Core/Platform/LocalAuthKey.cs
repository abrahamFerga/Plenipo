using Plenipo.Core.Entities;

namespace Plenipo.Core.Platform;

/// <summary>
/// A deployment-level cryptographic key for the embedded issuer (<c>Auth:Mode=Local</c>, ADR 0003):
/// one RSA signing key and one symmetric encryption key, generated on first Local-mode startup. The
/// material is protected at rest with Data Protection — whose key ring the host already refuses to
/// run without (Redis or <c>DataProtection:KeysPath</c>) outside Development — and never leaves the
/// deployment: it is what makes this deployment's tokens this deployment's tokens.
/// </summary>
public sealed class LocalAuthKey : EntityBase
{
    /// <summary>Key purpose: <c>sig</c> (RSA, signs JWTs) or <c>enc</c> (symmetric, wraps non-access tokens).</summary>
    public required string Use { get; set; }

    /// <summary>The key material (PKCS#8 or raw bytes, base64), Data-Protection-protected.</summary>
    public required string ProtectedKey { get; set; }
}

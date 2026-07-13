using System.Security.Cryptography;
using System.Text;

namespace Plenipo.Application.Ai;

/// <summary>
/// Identity for "the exact instructions that governed this reply". The effective prompt is
/// assembled per turn from moving parts (tenant system prompt, module manifest, agent profile,
/// skills advertisement) — the hash pins which assembly a given assistant message ran under,
/// and the snapshot store resolves a hash back to the full text for audit or reproduction.
/// </summary>
public static class InstructionHash
{
    /// <summary>Lowercase hex SHA-256 of the effective instruction text.</summary>
    public static string Compute(string instructions) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions)));
}

/// <summary>Records each distinct effective-instruction text once per tenant, keyed by hash.</summary>
public interface IInstructionSnapshotStore
{
    /// <summary>Ensures a snapshot exists for this hash (first-writer wins; races are benign).</summary>
    public Task EnsureAsync(string hash, string instructions, CancellationToken cancellationToken = default);
}

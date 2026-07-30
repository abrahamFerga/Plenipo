namespace Plenipo.Application.Bootstrap;

/// <summary>What a bootstrap attempt did. Every outcome is logged; only <see cref="Created"/> writes.</summary>
public enum BootstrapOutcome
{
    /// <summary>No <c>Bootstrap</c> section — the normal state of a deployment that is already running.</summary>
    NotConfigured = 0,

    /// <summary>The deployment already has an operator principal, so the section is inert.</summary>
    AlreadyBootstrapped = 1,

    /// <summary>The first tenant and its operator were created.</summary>
    Created = 2,

    /// <summary>Another instance won the race. Not an error — exactly one tenant exists either way.</summary>
    RaceLost = 3,

    /// <summary>The section was configured but could not be applied; <c>Detail</c> says why.</summary>
    Failed = 4,
}

public sealed record BootstrapResult(
    BootstrapOutcome Outcome,
    Guid TenantId = default,
    string? Slug = null,
    string? Detail = null);

/// <summary>
/// Creates the first tenant and its first operator on a deployment that has neither, from configuration
/// consumed once at startup. See <see cref="BootstrapOptions"/> for why this is not an HTTP surface.
/// </summary>
public interface IPlatformBootstrapper
{
    public Task<BootstrapResult> BootstrapAsync(CancellationToken cancellationToken = default);
}

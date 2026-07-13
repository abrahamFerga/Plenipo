namespace Plenipo.Application.Ai;

/// <summary>
/// Discovers the models currently exposed by an AI provider. Catalogs are fetched on demand so
/// Plenipo never carries a hardcoded list that drifts behind the provider.
/// </summary>
public interface IAiModelCatalog
{
    public Task<AiModelCatalogResult> DiscoverAsync(
        string provider,
        string? endpoint,
        string? apiKey,
        CancellationToken cancellationToken = default);
}

public sealed record AiModelCatalogResult(
    IReadOnlyList<string> Models,
    bool SupportsDiscovery = true,
    string? Message = null);

namespace Plenipo.Application.Ai;

/// <summary>
/// Discovers the models currently exposed by an AI provider. Catalogs are fetched on demand so
/// Plenipo never carries a hardcoded list that drifts behind the provider.
/// <para>
/// An implementation MAY narrow a catalog to the models a chat assistant can use — OpenAI returns
/// every modality it sells in one list — but must then say so in <see cref="AiModelCatalogResult.Message"/>
/// rather than hiding rows silently, because the admin's fallback is to type an id by hand and they
/// can only choose that if they know the list was narrowed.
/// </para>
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

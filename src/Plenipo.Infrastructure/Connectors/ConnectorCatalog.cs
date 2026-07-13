using System.Text.RegularExpressions;
using Plenipo.Application.Connectors;
using Plenipo.Connectors.Sdk;

namespace Plenipo.Infrastructure.Connectors;

/// <summary>
/// Validates and exposes the installed connectors' manifests, mirroring the module catalog:
/// an invalid registration (duplicate or malformed ids, tools without permissions) fails at
/// startup with a clear message, never on the first request.
/// </summary>
public sealed partial class ConnectorCatalog : IConnectorCatalog
{
    private readonly Dictionary<string, ConnectorManifest> _manifests;

    public ConnectorCatalog(IEnumerable<IConnector> connectors)
    {
        _manifests = new Dictionary<string, ConnectorManifest>(StringComparer.Ordinal);
        foreach (var connector in connectors)
        {
            var manifest = connector.Manifest;
            if (!ConnectorIdPattern().IsMatch(manifest.Id))
            {
                throw new InvalidOperationException(
                    $"Connector id '{manifest.Id}' is invalid: use lowercase letters, digits, and single hyphens.");
            }

            if (!_manifests.TryAdd(manifest.Id, manifest))
            {
                throw new InvalidOperationException($"Duplicate connector id '{manifest.Id}'.");
            }

            foreach (var tool in manifest.Tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Permission))
                {
                    throw new InvalidOperationException(
                        $"Connector '{manifest.Id}' tool '{tool.Name}' declares no permission.");
                }
            }
        }
    }

    public IReadOnlyList<ConnectorManifest> Manifests => [.. _manifests.Values];

    public bool TryGetManifest(string connectorId, out ConnectorManifest? manifest)
    {
        var found = _manifests.TryGetValue(connectorId, out var value);
        manifest = value;
        return found;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ConnectorIdPattern();
}

using Plenipo.Connectors.Sdk;
using Microsoft.Extensions.Hosting;

namespace Plenipo.Connectors;

/// <summary>
/// One-line registration of every built-in connector in this package — the in-box marketplace.
/// All of them appear on the admin Integrations page and all are default-off per tenant, so
/// shipping them costs nothing until a tenant admin explicitly enables one. Hosts that want to
/// suppress a connector entirely do it with <c>Connectors:Exclude</c> in configuration, not code.
/// </summary>
public static class BuiltInConnectors
{
    public static IHostApplicationBuilder AddPlenipoConnectors(this IHostApplicationBuilder builder) =>
        builder.AddPlenipoConnectorsFrom(typeof(BuiltInConnectors).Assembly);
}

using Plenipo.Connectors.Sdk;

namespace Plenipo.AspNetCore.Connectors;

/// <summary>
/// Host-side connector wiring, mirroring <c>AddPlenipoModule</c>: each installed connector is
/// instantiated once, registers its services and tool source, and is exposed as an
/// <see cref="IConnector"/> singleton so the catalog and admin surface can enumerate it.
/// Installation makes a connector <em>available</em>; a tenant admin still has to enable it
/// per tenant (connectors are default-off) before its tools exist for that tenant.
/// Registration honors <c>Connectors:Exclude</c>, so a deployment can suppress a compiled-in
/// connector by configuration alone. To register a whole package at once, see
/// <see cref="ConnectorRegistration.AddPlenipoConnectorsFrom"/> (or the built-in bundle's
/// <c>AddPlenipoConnectors()</c>).
/// </summary>
public static class ConnectorHostExtensions
{
    /// <summary>Registers a Plenipo connector in the host. Call once per connector in <c>Program.cs</c>.</summary>
    public static IHostApplicationBuilder AddPlenipoConnector<TConnector>(this IHostApplicationBuilder builder)
        where TConnector : class, IConnector, new()
        => builder.AddPlenipoConnectorInstance(new TConnector());
}

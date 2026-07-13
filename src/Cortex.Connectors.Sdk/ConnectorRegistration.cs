using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cortex.Connectors.Sdk;

/// <summary>
/// Assembly-scan registration — the config-gated half of ADR-0001's Option B. One call registers
/// every connector compiled into an assembly; a host then narrows with configuration
/// (<c>Connectors:Exclude</c>) instead of code. Which integrations a deployment OFFERS becomes an
/// ops decision; which ones a tenant USES remains the admin's per-tenant, default-off toggle.
/// </summary>
public static class ConnectorRegistration
{
    /// <summary>
    /// Configuration list of connector ids a host suppresses without recompiling,
    /// e.g. <c>Connectors:Exclude:0=s3</c> (or <c>Connectors__Exclude__0=s3</c> as an env var).
    /// </summary>
    public const string ExcludeKey = "Connectors:Exclude";

    public const string OperatorEnabledKey = "Connectors:OperatorEnabled";

    /// <summary>
    /// Registers every <see cref="IConnector"/> with a public parameterless constructor found in
    /// <paramref name="assemblies"/>, skipping ids listed under <c>Connectors:Exclude</c>.
    /// Deterministic order (type full name) so startup validation failures are reproducible.
    /// </summary>
    public static IHostApplicationBuilder AddCortexConnectorsFrom(
        this IHostApplicationBuilder builder, params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var connectorTypes = assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false }
                    && typeof(IConnector).IsAssignableFrom(t)
                    && t.GetConstructor(Type.EmptyTypes) is not null)
                .OrderBy(t => t.FullName, StringComparer.Ordinal);

            foreach (var type in connectorTypes)
            {
                builder.AddCortexConnectorInstance((IConnector)Activator.CreateInstance(type)!);
            }
        }

        return builder;
    }

    /// <summary>
    /// Registers one connector instance unless configuration excludes its id. The building block
    /// both the scan above and the per-type <c>AddCortexConnector&lt;T&gt;()</c> share, and the
    /// entry point for connectors without a parameterless constructor.
    /// </summary>
    public static IHostApplicationBuilder AddCortexConnectorInstance(
        this IHostApplicationBuilder builder, IConnector connector)
    {
        if (IsExcluded(builder.Configuration, connector.Manifest.Id))
        {
            return builder;
        }

        if (connector.Manifest.RequiresOperatorEnablement &&
            !builder.Configuration.GetValue<bool>($"{OperatorEnabledKey}:{connector.Manifest.Id}"))
        {
            return builder;
        }

        connector.RegisterServices(builder.Services, builder.Configuration);
        builder.Services.AddSingleton<IConnector>(connector);
        return builder;
    }

    /// <summary>Whether <c>Connectors:Exclude</c> lists the id (case-insensitive).</summary>
    public static bool IsExcluded(IConfiguration configuration, string connectorId) =>
        configuration.GetSection(ExcludeKey).GetChildren()
            .Any(c => string.Equals(c.Value, connectorId, StringComparison.OrdinalIgnoreCase));
}

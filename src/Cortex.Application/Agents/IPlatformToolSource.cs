using Cortex.Modules.Sdk;

namespace Cortex.Application.Agents;

/// <summary>
/// Supplies platform-wide tools that every module's agent receives (documents, files, …), in
/// addition to the module's own <see cref="IModuleToolSource"/> tools. Same contract: built inside
/// the request scope, each tool gated by its permission before the model ever sees the schema.
/// </summary>
public interface IPlatformToolSource
{
    /// <summary>Build the platform tools using services resolved from the current scope.</summary>
    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices);
}

/// <summary>First-class host extension point for platform-level tools.</summary>
public static class PlatformToolRegistration
{
    /// <summary>
    /// Adds a tool source whose tools are offered in EVERY module's chat (like the built-in
    /// document, skill, and knowledge tools) — for product-wide capabilities that belong to the
    /// host rather than one module. Tools still pass the same per-permission RBAC gate,
    /// approval flags, and audit as everything else.
    /// </summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddCortexPlatformTools<TSource>(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        where TSource : class, IPlatformToolSource
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<IPlatformToolSource, TSource>(services);
        return services;
    }
}

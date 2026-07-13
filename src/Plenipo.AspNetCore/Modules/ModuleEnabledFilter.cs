using Plenipo.Application.Modules;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.AspNetCore.Modules;

/// <summary>
/// Endpoint filter applied to every route a module maps. If the module is disabled for the caller's tenant,
/// its endpoints respond <c>404 Not Found</c> — so disabling a module makes it uninvocable everywhere (the
/// agent runner enforces the same for chat, and the workspace catalog hides it), not merely hidden from the
/// navigation. The per-tenant check resolves <see cref="ITenantModuleStore"/> from the request scope.
/// </summary>
public sealed class ModuleEnabledFilter(string moduleId) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var store = context.HttpContext.RequestServices.GetRequiredService<ITenantModuleStore>();
        if (!await store.IsEnabledAsync(moduleId, context.HttpContext.RequestAborted))
        {
            return Results.NotFound();
        }

        return await next(context);
    }
}

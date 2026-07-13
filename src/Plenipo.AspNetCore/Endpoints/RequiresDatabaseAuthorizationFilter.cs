using Plenipo.Application.Authorization;
using Microsoft.Extensions.Options;

namespace Plenipo.AspNetCore.Endpoints;

/// <summary>
/// Refuses internal user-level RBAC mutations (assign/revoke roles, grant/revoke permissions) when
/// <c>Auth:PermissionSource=Token</c>: with the external IdP as the single source of truth, writing
/// assignment rows the resolver would silently ignore is worse than an honest 409. Role
/// <em>baseline</em> editing stays available — baselines are what map IdP role names onto Plenipo's
/// fine-grained tool permissions.
/// </summary>
internal sealed class RequiresDatabaseAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var source = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<AuthorizationSourceOptions>>().Value;
        if (source.IsTokenSourced)
        {
            return Results.Conflict(new
            {
                error = "Authorization is sourced from the external identity provider (Auth:PermissionSource=Token). " +
                        "Assign roles in the IdP (e.g. Entra External ID app roles / B2C claims); " +
                        "internal role and permission assignments are disabled. " +
                        "Role → permission baselines remain editable under Roles.",
            });
        }

        return await next(context);
    }
}

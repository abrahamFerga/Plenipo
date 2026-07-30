using Plenipo.Infrastructure.Context;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace Plenipo.AspNetCore.Auth;

/// <summary>
/// Gives a 403 caused by an UNRESOLVED TENANT a body that names the cause.
///
/// <para>Permissions are only resolved after a tenant resolves, so an authenticated principal whose tenant
/// does not exist carries an empty permission set and is refused by every gated endpoint — with nothing to
/// distinguish "you lack this permission" from "this deployment does not know who you are". On a fresh
/// deployment that is the entire symptom of having no tenant at all, and it reads as a permissions bug.</para>
///
/// <para>Strictly additive: the default handler still makes the decision and still sets the status. This
/// only writes a ProblemDetails body where there was none, and only when the tenant is the reason. An
/// ordinary permission denial is byte-identical to before.</para>
/// </summary>
public sealed class UnresolvedTenantAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        // Only a genuine authorization failure (not a challenge, which is a 401 the client can act on).
        if (authorizeResult.Forbidden && context.User?.Identity?.IsAuthenticated == true)
        {
            // This handler is a singleton; RequestContext is scoped, so it must come from the request.
            var requestContext = context.RequestServices.GetService<RequestContext>();
            if (requestContext is not null && UnresolvedTenantProblem.Describe(requestContext) is { } detail)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                    title = UnresolvedTenantProblem.Title,
                    status = StatusCodes.Status403Forbidden,
                    detail,
                }, context.RequestAborted);
                return;
            }
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}

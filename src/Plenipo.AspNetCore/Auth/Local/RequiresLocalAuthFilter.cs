using Microsoft.Extensions.Options;

namespace Plenipo.AspNetCore.Auth.Local;

/// <summary>
/// Refuses local-credential endpoints when the deployment is NOT in <c>Auth:Mode=Local</c>: with an
/// external IdP in charge of credentials, minting password rows the sign-in path would never consult
/// is worse than an honest 409 (the <c>RequiresDatabaseAuthorizationFilter</c> precedent).
/// </summary>
internal sealed class RequiresLocalAuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var auth = context.HttpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (!auth.IsLocalMode)
        {
            return Results.Conflict(new
            {
                error = "This deployment does not use built-in sign-in (Auth:Mode=Local). " +
                        "Credentials are managed at the external identity provider.",
            });
        }

        return await next(context);
    }
}

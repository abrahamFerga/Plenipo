using Plenipo.AspNetCore.Identity;

namespace Plenipo.AspNetCore.Middleware;

/// <summary>
/// Runs after authentication and before authorization, delegating to <see cref="IRequestEnricher"/> to
/// populate the scoped request context (tenant → user → permissions) for authenticated HTTP requests.
/// </summary>
public sealed class RequestEnrichmentMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IRequestEnricher enricher)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var allowed = await enricher.EnrichAsync(
                context.User, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);
            if (!allowed)
            {
                // The user is authenticated but deactivated — deny without invoking any endpoint.
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await next(context);
    }
}

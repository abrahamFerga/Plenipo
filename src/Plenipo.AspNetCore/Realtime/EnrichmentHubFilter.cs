using Plenipo.AspNetCore.Auth;
using Plenipo.AspNetCore.Identity;
using Plenipo.Infrastructure.Context;
using Microsoft.AspNetCore.SignalR;

namespace Plenipo.AspNetCore.Realtime;

/// <summary>
/// Applies the same identity enrichment to SignalR hub invocations that the HTTP middleware applies to
/// REST calls — hub method calls don't traverse the HTTP middleware pipeline, so without this the
/// request context would be empty for chat-over-WebSocket.
/// </summary>
public sealed class EnrichmentHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var principal = invocationContext.Context.User;
        if (principal?.Identity?.IsAuthenticated == true)
        {
            var enricher = invocationContext.ServiceProvider.GetRequiredService<IRequestEnricher>();
            var allowed = await enricher.EnrichAsync(principal, null, invocationContext.Context.ConnectionAborted);
            if (!allowed)
            {
                // Deactivated account — refuse the hub invocation (chat-over-WebSocket) too.
                throw new HubException("This account is deactivated.");
            }

            // An unresolved tenant leaves the context permission-less, so every gated hub method would
            // fail with nothing to say about why. Name the cause here, matching what REST answers.
            var requestContext = invocationContext.ServiceProvider.GetRequiredService<RequestContext>();
            if (UnresolvedTenantProblem.Describe(requestContext) is { } problem)
            {
                throw new HubException(problem);
            }
        }

        return await next(invocationContext);
    }
}

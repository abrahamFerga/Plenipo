using Plenipo.Application.Auditing;
using Plenipo.Application.Authorization;
using Plenipo.Core.Identity;
using Plenipo.Infrastructure.Context;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Plenipo.AspNetCore.Auth;

/// <summary>
/// The platform's authorization result handler. Two additions to the default, both on a 403 for an
/// authenticated caller:
///
/// <para><b>The denial is recorded</b> (#115). <c>AccessDenied</c> existed in the auth-event vocabulary
/// and nothing wrote it, so "did anyone try to approve their own proposal?" could not be answered from
/// the trail. One row per denied request, naming the method, the path and the permission the policy
/// required — never more, because a denial recorded twice misleads as much as one not recorded.</para>
///
/// <para><b>An UNRESOLVED TENANT gets a body that names the cause.</b> Permissions are only resolved
/// after a tenant resolves, so an authenticated principal whose tenant does not exist carries an empty
/// permission set and is refused by every gated endpoint — with nothing to distinguish "you lack this
/// permission" from "this deployment does not know who you are". On a fresh deployment that is the
/// entire symptom of having no tenant at all, and it reads as a permissions bug.</para>
///
/// <para>Strictly additive: the default handler still makes the decision and still sets the status.
/// An ordinary permission denial is byte-identical on the wire to before.</para>
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
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        // Only a genuine authorization failure (not a challenge, which is a 401 the client can act on).
        if (authorizeResult.Forbidden && context.User?.Identity?.IsAuthenticated == true)
        {
            // This handler is a singleton; RequestContext is scoped, so it must come from the request.
            var requestContext = context.RequestServices.GetService<RequestContext>();
            var unresolved = requestContext is not null ? UnresolvedTenantProblem.Describe(requestContext) : null;

            await RecordDenialAsync(context, policy, unresolved);

            if (unresolved is not null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                    title = UnresolvedTenantProblem.Title,
                    status = StatusCodes.Status403Forbidden,
                    detail = unresolved,
                }, context.RequestAborted);
                return;
            }
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private static async Task RecordDenialAsync(HttpContext context, AuthorizationPolicy policy, string? unresolvedTenant)
    {
        var audit = context.RequestServices.GetService<IAuditLog>();
        if (audit is null)
        {
            return;
        }

        var required = policy.Requirements.OfType<PermissionRequirement>().Select(r => r.Permission).ToArray();
        var detail = $"{context.Request.Method} {context.Request.Path} requires " +
                     (required.Length > 0 ? string.Join(", ", required) : "an authorization policy the caller does not satisfy");
        if (unresolvedTenant is not null)
        {
            detail += $"; {unresolvedTenant}";
        }

        var current = context.RequestServices.GetService<ICurrentUser>();
        try
        {
            await audit.RecordAuthEventAsync(new AuthAuditEntry
            {
                TenantId = current?.TenantId,
                UserId = current?.UserId,
                Subject = current?.Subject ?? context.User.FindFirst("sub")?.Value,
                UserDisplay = current?.DisplayName,
                EventType = AuthAuditEventType.AccessDenied,
                Detail = detail,
                IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            }, context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The denial itself must still be answered; the audit store is best-effort by contract.
            context.RequestServices.GetService<ILogger<UnresolvedTenantAuthorizationResultHandler>>()
                ?.LogError(ex, "Could not record an access denial for {Method} {Path}.", context.Request.Method, context.Request.Path);
        }
    }
}

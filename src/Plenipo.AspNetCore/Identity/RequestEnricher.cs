using System.Security.Claims;
using Plenipo.Application.Auditing;
using Plenipo.Application.Authorization;
using Plenipo.AspNetCore.Auth;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Context;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Plenipo.AspNetCore.Identity;

/// <summary>
/// Populates the scoped <see cref="RequestContext"/> from an authenticated principal: resolve tenant →
/// JIT-provision the user → resolve permissions. Shared by the HTTP middleware and the SignalR hub
/// filter so chat-over-WebSocket gets the same identity treatment as REST calls.
/// </summary>
public interface IRequestEnricher
{
    /// <summary>
    /// Populates the request context from the principal. Returns <c>false</c> when the resolved user is
    /// <b>deactivated</b> — the caller must then deny the request (the user keeps a valid token but no access).
    /// </summary>
    public Task<bool> EnrichAsync(ClaimsPrincipal principal, string? ipAddress, CancellationToken cancellationToken);
}

public sealed class RequestEnricher(
    RequestContext requestContext,
    PlatformDbContext db,
    IPermissionResolver permissionResolver,
    IAuditLog auditLog,
    IOptions<AuthOptions> authOptions,
    IOptions<AuthorizationSourceOptions> authorizationSource) : IRequestEnricher
{
    public async Task<bool> EnrichAsync(ClaimsPrincipal principal, string? ipAddress, CancellationToken cancellationToken)
    {
        var subject = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (subject is null)
        {
            return true;
        }

        var name = principal.FindFirstValue("name") ?? principal.Identity?.Name;
        var email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? principal.FindFirstValue("preferred_username")
            ?? subject;

        requestContext.SetIdentity(subject, name);

        var tenant = await ResolveTenantAsync(principal.FindFirstValue(authOptions.Value.TenantClaim), cancellationToken);
        if (tenant is null)
        {
            return true;
        }

        // A deactivated tenant denies every one of its users — a tenant-wide kill switch for an operator.
        if (!tenant.IsActive)
        {
            return false;
        }

        requestContext.SetTenant(tenant.Id);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Subject == subject, cancellationToken);

        // A deactivated user keeps a valid token but no access: deny before resolving any identity or
        // permissions. (A just-provisioned user is active by default, so this only affects existing users.)
        if (user is { IsActive: false })
        {
            return false;
        }

        if (user is null)
        {
            // Seat limit from the subscription (Tenant.MaxSeats; null = unlimited): a full tenant
            // admits no NEW users. Existing users keep signing in; deactivating one frees a seat.
            if (tenant.MaxSeats is { } maxSeats)
            {
                var activeSeats = await db.Users.CountAsync(u => u.IsActive, cancellationToken);
                if (activeSeats >= maxSeats)
                {
                    await auditLog.RecordAuthEventAsync(new AuthAuditEntry
                    {
                        TenantId = tenant.Id,
                        Subject = subject,
                        UserDisplay = name,
                        EventType = AuthAuditEventType.SeatLimitDenied,
                        Detail = $"seat limit {maxSeats} reached",
                        IpAddress = ipAddress,
                    }, cancellationToken);
                    return false;
                }
            }

            user = new User
            {
                TenantId = tenant.Id,
                Subject = subject,
                Email = email ?? subject,
                DisplayName = name,
            };

            // A standing invite for this email (created in Admin → Users before the person ever
            // signed in) defines the starting roles; the address is the key, no token link needed.
            var normalizedEmail = (email ?? subject).Trim().ToLowerInvariant();
            var invite = await db.UserInvites.IgnoreQueryFilters().FirstOrDefaultAsync(
                i => i.TenantId == tenant.Id && i.RedeemedAt == null && i.Email == normalizedEmail,
                cancellationToken);
            if (invite is not null && invite.RoleList() is { Length: > 0 } invitedRoles)
            {
                foreach (var role in invitedRoles)
                {
                    user.Roles.Add(new UserRole { TenantId = tenant.Id, UserId = user.Id, Role = role });
                }
            }
            else
            {
                // Default the new user to the configured role (Auth:DefaultRole, "user" unless the
                // product overrides it) ONLY when the token asserts no roles of its own. A principal
                // whose IdP already scopes it (e.g. "guest") must not silently escalate via a DB role
                // the platform invented. In Token mode the platform NEVER invents a role — the
                // external IdP is the single authority, so a role-less token means a permission-less
                // user until the IdP says otherwise.
                var hasTokenRoles = principal.FindAll(ClaimTypes.Role).Concat(principal.FindAll("roles")).Any();
                if (!hasTokenRoles && !authorizationSource.Value.IsTokenSourced &&
                    !string.IsNullOrWhiteSpace(authorizationSource.Value.DefaultRole))
                {
                    user.Roles.Add(new UserRole { TenantId = tenant.Id, UserId = user.Id, Role = authorizationSource.Value.DefaultRole });
                }
            }

            if (invite is not null)
            {
                invite.RedeemedAt = DateTimeOffset.UtcNow;
                invite.RedeemedByUserId = user.Id;
            }

            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);

            await auditLog.RecordAuthEventAsync(new AuthAuditEntry
            {
                TenantId = tenant.Id,
                UserId = user.Id,
                Subject = subject,
                UserDisplay = name,
                EventType = AuthAuditEventType.UserProvisioned,
                IpAddress = ipAddress,
            }, cancellationToken);

            if (invite is not null)
            {
                await auditLog.RecordAuthEventAsync(new AuthAuditEntry
                {
                    TenantId = tenant.Id,
                    UserId = user.Id,
                    Subject = subject,
                    UserDisplay = name,
                    EventType = AuthAuditEventType.InviteRedeemed,
                    Detail = invite.Roles.Length > 0 ? $"roles: {invite.Roles}" : "default role",
                    IpAddress = ipAddress,
                }, cancellationToken);
            }
        }
        else
        {
            user.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        requestContext.SetUser(user.Id, subject, name);
        requestContext.SetPermissions(await permissionResolver.ResolveAsync(principal, cancellationToken));
        return true;
    }

    private async Task<Tenant?> ResolveTenantAsync(string? slug, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(slug))
        {
            return await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        }

        return await db.Tenants.CountAsync(cancellationToken) == 1
            ? await db.Tenants.FirstAsync(cancellationToken)
            : null;
    }
}

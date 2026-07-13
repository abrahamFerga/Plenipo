using Plenipo.Application.Authorization;
using Plenipo.Application.Notifications;
using Plenipo.Core.Identity;
using Plenipo.Core.Multitenancy;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Plenipo.AspNetCore.Endpoints;

/// <summary>
/// Standing email invites (Admin → Users): name an address and starting roles BEFORE the person
/// ever signs in; the request enricher applies them at first sign-in and marks the invite
/// redeemed. Email delivery is best-effort through the existing SMTP seam — an unconfigured
/// mail server never blocks inviting (the admin shares the sign-in link manually instead).
/// </summary>
public static class UserInviteEndpoints
{
    public static void MapUserInviteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users/invites")
            .WithTags("Admin")
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageUsers));

        // Newest first, redeemed included (with the marker) so the list doubles as a short history.
        group.MapGet("/", async (PlatformDbContext db, CancellationToken ct) =>
            {
                var invites = await db.UserInvites
                    .OrderByDescending(i => i.CreatedAt)
                    .Take(100)
                    .ToListAsync(ct);
                return Results.Ok(invites.Select(i => new InviteDto(
                    i.Id, i.Email, i.RoleList(), i.CreatedAt, i.RedeemedAt)));
            })
            .WithName("Admin_ListInvites");

        group.MapPost("/", async (
                CreateInviteRequest body, PlatformDbContext db, ITenantContext tenant,
                ISmtpTransport smtp, IConfiguration configuration, HttpContext http, ICurrentUser current,
                ILoggerFactory loggerFactory, CancellationToken ct) =>
            {
                var email = body.Email?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length > 320)
                {
                    return Results.BadRequest(new { error = "A valid email address is required." });
                }

                // EF translates ToLower() to SQL LOWER(); string.Equals(…, OrdinalIgnoreCase) would not translate.
#pragma warning disable CA1862
                if (await db.Users.AnyAsync(u => u.Email.ToLower() == email, ct))
#pragma warning restore CA1862
                {
                    return Results.BadRequest(new { error = $"{email} is already a member of this tenant." });
                }
                if (await db.UserInvites.AnyAsync(i => i.Email == email && i.RedeemedAt == null, ct))
                {
                    return Results.BadRequest(new { error = $"{email} already has a pending invite." });
                }

                var roles = (body.Roles ?? [])
                    .Select(r => r.Trim())
                    .Where(r => r.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                // Inviting with a role is an authorization mutation, exactly like assigning a role
                // to an existing user. A user manager may still send a role-less/default-role invite,
                // but only a role manager may choose explicit roles.
                if (roles.Length > 0 && !current.HasPermission(Permissions.ManageRoles))
                {
                    return Results.Forbid();
                }

                foreach (var role in roles)
                {
                    if (!Roles.All.Contains(role, StringComparer.Ordinal) &&
                        !await db.RolePermissions.AnyAsync(r => r.Role == role, ct))
                    {
                        return Results.BadRequest(new { error = $"Unknown role '{role}'." });
                    }

                    var grants = await db.RolePermissions
                        .Where(r => r.Role == role)
                        .Select(r => r.Permission)
                        .ToListAsync(ct);
                    if (grants.Count == 0)
                    {
                        grants = RolePermissions.ForRole(role).ToList();
                    }

                    var forbidden = PermissionGrantValidator.FindForbiddenGrants(
                        grants, Permissions.OperatorOnly, current.HasPermission);
                    if (forbidden.Count > 0)
                    {
                        return Results.BadRequest(new
                        {
                            error = $"The '{role}' role grants operator-reserved permissions and cannot be invited by this caller.",
                        });
                    }
                }

                var invite = new UserInvite
                {
                    TenantId = tenant.RequireTenantId(),
                    Email = email,
                    Roles = string.Join(",", roles),
                };
                db.UserInvites.Add(invite);
                await db.SaveChangesAsync(ct);

                // Best-effort mail: the product's name + the deployment's own origin as the link.
                var emailSent = false;
                if (smtp.IsConfigured)
                {
                    var product = configuration["Branding:ProductName"] ?? "Plenipo";
                    var link = $"{http.Request.Scheme}://{http.Request.Host}";
                    try
                    {
                        await smtp.SendAsync(new EmailMessage(
                            email,
                            $"You're invited to {product}",
                            $"You've been invited to {product}.\n\nSign in with this email address to get started: {link}\n"), ct);
                        emailSent = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        loggerFactory.CreateLogger("Plenipo.Invites")
                            .LogWarning(ex, "Invite created but the email to {Email} failed", email);
                    }
                }

                return Results.Ok(new
                {
                    id = invite.Id,
                    emailSent,
                    message = emailSent
                        ? $"Invited {email} — they'll get their roles at first sign-in."
                        : $"Invite recorded for {email}. Email isn't configured (Email:*), so share the sign-in link yourself; their roles apply at first sign-in.",
                });
            })
            .WithName("Admin_CreateInvite");

        // Revoking only makes sense while pending; a redeemed invite is history, not access
        // (removing the user's access is Users → deactivate / role removal).
        group.MapDelete("/{id:guid}", async (Guid id, PlatformDbContext db, CancellationToken ct) =>
            {
                var invite = await db.UserInvites.FirstOrDefaultAsync(i => i.Id == id && i.RedeemedAt == null, ct);
                if (invite is null)
                {
                    return Results.NotFound();
                }

                db.UserInvites.Remove(invite);
                await db.SaveChangesAsync(ct);
                return Results.Ok();
            })
            .WithName("Admin_RevokeInvite");
    }

    private sealed record InviteDto(
        Guid Id, string Email, string[] Roles, DateTimeOffset CreatedAt, DateTimeOffset? RedeemedAt);

    public sealed record CreateInviteRequest(string? Email, string[]? Roles);
}

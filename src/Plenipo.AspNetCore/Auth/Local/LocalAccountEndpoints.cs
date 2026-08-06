using Plenipo.Application.Auditing;
using Plenipo.Application.Authorization;
using Plenipo.Core.Identity;
using Plenipo.Core.Multitenancy;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.LocalAuth;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Plenipo.AspNetCore.Auth.Local;

/// <summary>
/// Credential management for local auth mode (ADR 0003), answering 409 in every other mode.
/// Self-service under <c>/api/auth</c>: change password, enroll/confirm/remove TOTP. Admin under
/// <c>/api/admin/users/local</c>: create a user with a temporary password (the no-SMTP on-prem path
/// email invites can't cover), reset, unlock, reset TOTP — behind the same permissions the rest of
/// user management uses. Temporary passwords appear in exactly one response and are never
/// reconstructable.
/// </summary>
public static class LocalAccountEndpoints
{
    public static void MapPlenipoLocalAccountEndpoints(this IEndpointRouteBuilder app)
    {
        MapSelfService(app);
        MapAdmin(app);
    }

    private static void MapSelfService(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("LocalAuth")
            .RequireAuthorization()
            .AddEndpointFilter<RequiresLocalAuthFilter>();

        // What the shell needs to render a security panel — and nothing an attacker couldn't infer.
        group.MapGet("/local-status", async (
                ICurrentUser current, LocalCredentialService credentials, CancellationToken ct) =>
            {
                if (current.UserId is not { } userId)
                {
                    return Results.NotFound();
                }

                var credential = await credentials.FindForUserAsync(userId, ct);
                return Results.Ok(new LocalStatusDto(
                    HasCredential: credential is not null,
                    TotpEnabled: credential?.TotpEnabledAt is not null,
                    MustChangePassword: credential?.MustChangePassword ?? false));
            })
            .WithName("LocalAuth_Status");

        group.MapPost("/password", async (
                ChangePasswordRequest body, ICurrentUser current, LocalCredentialService credentials,
                HttpContext http, CancellationToken ct) =>
            {
                if (current.UserId is not { } userId
                    || await credentials.FindForUserAsync(userId, ct) is not { } credential)
                {
                    return Results.NotFound();
                }

                if (LocalCredentialService.IsLockedOut(credential))
                {
                    return Results.BadRequest(new { error = "Too many attempts. Try again later." });
                }

                if (!await credentials.VerifyPasswordAsync(credential, body.CurrentPassword ?? "", ct))
                {
                    await credentials.RegisterFailureAsync(
                        credential, "wrong password (change attempt)", ClientIp(http), ct);
                    return Results.BadRequest(new { error = "The current password is not right." });
                }

                if (await credentials.SetPasswordAsync(
                        credential, body.NewPassword ?? "", mustChange: false, byAdminReset: false,
                        ClientIp(http), ct) is { } error)
                {
                    return Results.BadRequest(new { error });
                }

                return Results.Ok(new
                {
                    message = "Password changed. Other sessions will be signed out when their tokens next refresh.",
                });
            })
            .WithName("LocalAuth_ChangePassword");

        group.MapPost("/totp/enroll", async (
                ICurrentUser current, LocalCredentialService credentials, IConfiguration configuration,
                CancellationToken ct) =>
            {
                if (current.UserId is not { } userId
                    || await credentials.FindForUserAsync(userId, ct) is not { } credential)
                {
                    return Results.NotFound();
                }

                if (credential.TotpEnabledAt is not null)
                {
                    return Results.BadRequest(new { error = "Two-factor is already enabled. Remove it first to re-enroll." });
                }

                var secret = await credentials.StartTotpEnrollmentAsync(credential, ct);
                var product = configuration["Branding:ProductName"] ?? "Plenipo";
                return Results.Ok(new
                {
                    secret,
                    otpauthUri = Totp.BuildOtpAuthUri(product, credential.Email, secret),
                    message = "Add the key to your authenticator app, then confirm with a code. " +
                              "Nothing changes until a code is confirmed.",
                });
            })
            .WithName("LocalAuth_TotpEnroll");

        group.MapPost("/totp/confirm", async (
                TotpCodeRequest body, ICurrentUser current, LocalCredentialService credentials,
                HttpContext http, CancellationToken ct) =>
            {
                if (current.UserId is not { } userId
                    || await credentials.FindForUserAsync(userId, ct) is not { } credential)
                {
                    return Results.NotFound();
                }

                return await credentials.ConfirmTotpEnrollmentAsync(credential, body.Code ?? "", ClientIp(http), ct)
                    ? Results.Ok(new { message = "Two-factor is on. You'll be asked for a code at sign-in." })
                    : Results.BadRequest(new { error = "That code is not right. Codes change every 30 seconds." });
            })
            .WithName("LocalAuth_TotpConfirm");

        // Turning MFA OFF needs a current code — a hijacked session must not be able to weaken the
        // account it rode in on. (POST, not DELETE: a DELETE body is refused by minimal APIs and
        // stripped by some proxies, and this one is load-bearing.)
        group.MapPost("/totp/disable", async (
                TotpCodeRequest body, ICurrentUser current, LocalCredentialService credentials,
                HttpContext http, CancellationToken ct) =>
            {
                if (current.UserId is not { } userId
                    || await credentials.FindForUserAsync(userId, ct) is not { } credential)
                {
                    return Results.NotFound();
                }

                if (credential.TotpEnabledAt is null)
                {
                    return Results.BadRequest(new { error = "Two-factor is not enabled." });
                }

                if (!credentials.VerifyTotp(credential, body.Code ?? ""))
                {
                    await credentials.RegisterFailureAsync(
                        credential, "wrong totp code (disable attempt)", ClientIp(http), ct);
                    return Results.BadRequest(new { error = "That code is not right." });
                }

                await credentials.DisableTotpAsync(credential, "removed by the user", ClientIp(http), ct);
                return Results.Ok(new { message = "Two-factor is off." });
            })
            .WithName("LocalAuth_TotpDisable");
    }

    private static void MapAdmin(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users/local").WithTags("Admin")
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageUsers))
            .AddEndpointFilter<RequiresLocalAuthFilter>();

        group.MapGet("/", async (PlatformDbContext db, CancellationToken ct) =>
            {
                var rows = await (
                    from c in db.LocalCredentials
                    join u in db.Users on c.UserId equals u.Id
                    orderby c.Email
                    select new LocalUserDto(
                        u.Id, c.Email, u.DisplayName, u.IsActive, c.MustChangePassword,
                        c.LockedUntil, c.TotpEnabledAt != null, c.LastSignInAt)).ToListAsync(ct);
                return Results.Ok(rows);
            })
            .WithName("Admin_ListLocalUsers");

        group.MapPost("/", async (
                CreateLocalUserRequest body, PlatformDbContext db, ITenantContext tenant, ICurrentUser current,
                LocalCredentialService credentials, IEnumerable<ProductRole> productRoles,
                IOptions<AuthorizationSourceOptions> authorizationSource, IAuditLog auditLog,
                HttpContext http, CancellationToken ct) =>
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

                // Same deployment-wide rule CreateAsync enforces, surfaced before any row is written.
                if (await db.LocalCredentials.IgnoreQueryFilters().AnyAsync(c => c.Email == email, ct))
                {
                    return Results.BadRequest(new
                    {
                        error = $"{email} already has local sign-in on this deployment (possibly in another tenant).",
                    });
                }

                var roles = (body.Roles ?? [])
                    .Select(r => r.Trim())
                    .Where(r => r.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                // Mirrors the invite endpoint's guards VERBATIM rather than refactoring them into a
                // shared helper: creating with a role is an authorization mutation, and the invite
                // file's guard lines are spine-protected — duplication here is the deliberate choice.
                if (roles.Length > 0 && !current.HasPermission(Permissions.ManageRoles))
                {
                    return Results.Forbid();
                }

                var baseline = RoleBaseline.Merge(productRoles);
                var granted = await GroupedByRoleAsync(
                    db.RolePermissions.Select(r => new RoleRow(r.Role, r.Permission)), ct);
                var suppressed = await GroupedByRoleAsync(
                    db.RolePermissionSuppressions.Select(r => new RoleRow(r.Role, r.Permission)), ct);

                foreach (var role in roles)
                {
                    if (!RoleGrants.IsKnown(role, baseline, granted.Keys))
                    {
                        return Results.BadRequest(new { error = $"Unknown role '{role}'." });
                    }

                    var grants = string.Equals(role, Roles.SystemAdmin, StringComparison.Ordinal)
                        ? ["*"]
                        : RoleGrants.Effective(role, baseline, granted, suppressed);

                    var forbidden = PermissionGrantValidator.FindForbiddenGrants(
                        grants, Permissions.OperatorOnly, current.HasPermission);
                    if (forbidden.Count > 0)
                    {
                        return Results.BadRequest(new
                        {
                            error = $"The '{role}' role grants operator-reserved permissions and cannot be assigned by this caller.",
                        });
                    }
                }

                // Same seat rule the JIT path enforces: a full tenant admits no NEW users.
                var tenantId = tenant.RequireTenantId();
                var tenantRow = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
                if (tenantRow.MaxSeats is { } maxSeats
                    && await db.Users.CountAsync(u => u.IsActive, ct) >= maxSeats)
                {
                    return Results.BadRequest(new { error = $"The subscription's seat limit ({maxSeats}) is reached." });
                }

                var user = new User
                {
                    TenantId = tenantId,
                    // Local subjects are minted, never derived from the email — emails change.
                    Subject = $"local|{Guid.CreateVersion7():N}",
                    Email = email,
                    DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(),
                };

                var effectiveRoles = roles.Length > 0
                    ? roles
                    : string.IsNullOrWhiteSpace(authorizationSource.Value.DefaultRole)
                        ? []
                        : [authorizationSource.Value.DefaultRole];
                foreach (var role in effectiveRoles)
                {
                    user.Roles.Add(new UserRole { TenantId = tenantId, UserId = user.Id, Role = role });
                }

                db.Users.Add(user);
                await db.SaveChangesAsync(ct);

                var (_, temporaryPassword, error) = await credentials.CreateAsync(user, null, ClientIp(http), ct);
                if (error is not null)
                {
                    // Only reachable on a create race; leave no half-user behind.
                    db.Users.Remove(user);
                    await db.SaveChangesAsync(ct);
                    return Results.BadRequest(new { error });
                }

                await auditLog.RecordAuthEventAsync(new AuthAuditEntry
                {
                    TenantId = tenantId,
                    UserId = user.Id,
                    Subject = user.Subject,
                    UserDisplay = user.DisplayName,
                    EventType = AuthAuditEventType.UserProvisioned,
                    Detail = $"created by admin with local sign-in, roles: {string.Join(", ", effectiveRoles)}",
                    IpAddress = ClientIp(http),
                }, ct);

                return Results.Ok(new
                {
                    userId = user.Id,
                    email,
                    temporaryPassword,
                    message = $"Share the temporary password with {email} securely — it is shown only this once, " +
                              "and they must change it at first sign-in.",
                });
            })
            .WithName("Admin_CreateLocalUser");

        group.MapPost("/{userId:guid}/reset-password", async (
                Guid userId, LocalCredentialService credentials, HttpContext http, CancellationToken ct) =>
            {
                if (await credentials.FindForUserAsync(userId, ct) is not { } credential)
                {
                    return Results.NotFound();
                }

                var temporaryPassword = await credentials.ResetToTemporaryAsync(credential, ClientIp(http), ct);
                return Results.Ok(new
                {
                    temporaryPassword,
                    message = "Existing sessions end as their tokens refresh; a change is forced at next sign-in.",
                });
            })
            .WithName("Admin_ResetLocalPassword");

        group.MapPost("/{userId:guid}/unlock", async (
                Guid userId, LocalCredentialService credentials, HttpContext http, CancellationToken ct) =>
            {
                if (await credentials.FindForUserAsync(userId, ct) is not { } credential)
                {
                    return Results.NotFound();
                }

                await credentials.UnlockAsync(credential, ClientIp(http), ct);
                return Results.Ok(new { message = "Unlocked." });
            })
            .WithName("Admin_UnlockLocalUser");

        // Admin MFA reset is the on-prem recovery path for a lost phone (ADR 0003: no recovery codes).
        group.MapDelete("/{userId:guid}/totp", async (
                Guid userId, LocalCredentialService credentials, HttpContext http, CancellationToken ct) =>
            {
                if (await credentials.FindForUserAsync(userId, ct) is not { } credential)
                {
                    return Results.NotFound();
                }

                if (credential.TotpEnabledAt is null && credential.TotpSecret is null)
                {
                    return Results.BadRequest(new { error = "Two-factor is not enabled for this user." });
                }

                await credentials.DisableTotpAsync(credential, "reset by an admin", ClientIp(http), ct);
                return Results.Ok(new { message = "Two-factor removed. The user can re-enroll from their security settings." });
            })
            .WithName("Admin_ResetLocalTotp");
    }

    private static string? ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    private sealed record RoleRow(string Role, string Permission);

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GroupedByRoleAsync(
        IQueryable<RoleRow> rows, CancellationToken ct) =>
        (await rows.ToListAsync(ct))
            .GroupBy(r => r.Role, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Permission).ToList(),
                StringComparer.Ordinal);

    private sealed record LocalStatusDto(bool HasCredential, bool TotpEnabled, bool MustChangePassword);

    private sealed record LocalUserDto(
        Guid UserId, string Email, string? DisplayName, bool IsActive, bool MustChangePassword,
        DateTimeOffset? LockedUntil, bool TotpEnabled, DateTimeOffset? LastSignInAt);

    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

    public sealed record TotpCodeRequest(string? Code);

    public sealed record CreateLocalUserRequest(string? Email, string? DisplayName, string[]? Roles);
}

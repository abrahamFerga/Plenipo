using Plenipo.Application.Modules;
using Plenipo.Application.Notifications;
using Plenipo.Core.Identity;
using Plenipo.Core.Multitenancy;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Plenipo.AspNetCore.Endpoints;

/// <summary>
/// The current user's in-app notification inbox. Strictly self-scoped: every query filters by the
/// caller's user id on top of the tenant filter — there is no cross-user read, and no admin
/// endpoint exposes other people's inboxes.
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();

        MapDeviceEndpoints(group);

        // Inbox: unread first, newest first. ?unreadOnly=true for badge polling.
        group.MapGet("/", async (
            bool? unreadOnly, PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            if (current.UserId is not Guid userId)
            {
                return Results.BadRequest("No authenticated user.");
            }

            var query = db.UserNotifications.Where(n => n.UserId == userId);
            if (unreadOnly == true)
            {
                query = query.Where(n => n.ReadAt == null);
            }

            var items = await query
                .OrderBy(n => n.ReadAt != null)
                .ThenByDescending(n => n.CreatedAt)
                .Take(50)
                .Select(n => new NotificationDto(n.Id, n.Category, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt))
                .ToListAsync(ct);
            return Results.Ok(items);
        })
        .WithName("Notifications_List");

        group.MapPost("/{id:guid}/read", async (
            Guid id, PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            var notification = await db.UserNotifications
                .FirstOrDefaultAsync(n => n.Id == id && n.UserId == current.UserId, ct);
            if (notification is null)
            {
                return Results.NotFound();
            }

            notification.ReadAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithName("Notifications_MarkRead");

        group.MapPost("/read-all", async (PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            var unread = await db.UserNotifications
                .Where(n => n.UserId == current.UserId && n.ReadAt == null)
                .ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;
            foreach (var n in unread)
            {
                n.ReadAt = now;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { marked = unread.Count });
        })
        .WithName("Notifications_MarkAllRead");

        // The mute switchboard: every category any installed module declares, with the caller's
        // current stance. No stored row = on; a mute suppresses in-app and channels alike.
        group.MapGet("/preferences", async (
            IModuleCatalog catalog, PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            if (current.UserId is not Guid userId)
            {
                return Results.BadRequest("No authenticated user.");
            }

            var stored = await db.UserNotificationPreferences
                .Where(p => p.UserId == userId)
                .ToListAsync(ct);
            var categories = catalog.Manifests
                .SelectMany(m => m.NotificationCategories.Select(c => new PreferenceDto(
                    c.Id, c.Label, c.Description, m.Id,
                    stored.FirstOrDefault(p => p.Category == c.Id)?.Enabled ?? true)))
                .OrderBy(c => c.ModuleId, StringComparer.Ordinal)
                .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Results.Ok(categories);
        })
        .WithName("Notifications_Preferences");

        group.MapPut("/preferences/{category}", async (
            string category, PreferenceUpdate body, IModuleCatalog catalog, PlatformDbContext db,
            ICurrentUser current, ITenantContext tenant, CancellationToken ct) =>
        {
            if (current.UserId is not Guid userId)
            {
                return Results.BadRequest("No authenticated user.");
            }

            var declared = catalog.Manifests.Any(m => m.NotificationCategories.Any(c =>
                string.Equals(c.Id, category, StringComparison.Ordinal)));
            if (!declared)
            {
                return Results.NotFound(new { error = $"No module declares the notification category '{category}'." });
            }

            var preference = await db.UserNotificationPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId && p.Category == category, ct);
            if (preference is null)
            {
                preference = new UserNotificationPreference
                {
                    TenantId = tenant.RequireTenantId(),
                    UserId = userId,
                    Category = category,
                };
                db.UserNotificationPreferences.Add(preference);
            }

            preference.Enabled = body.Enabled;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { category, enabled = preference.Enabled });
        })
        .WithName("Notifications_UpdatePreference");
    }

    /// <summary>
    /// The caller's own push devices. Self-scoped like the inbox: a caller can only see, register,
    /// and forget their own installations, and no admin endpoint lists anyone else's.
    /// </summary>
    private static void MapDeviceEndpoints(RouteGroupBuilder group)
    {
        // Register (or re-register) THIS installation. The mobile shell calls this after the user
        // grants notification permission and on every launch afterwards, because push tokens
        // rotate — matching on installationId turns that into an update instead of a duplicate.
        group.MapPut("/devices", async (
            DeviceRegistration body, PlatformDbContext db, ICurrentUser current, ITenantContext tenant,
            IOptions<PushOptions> push, CancellationToken ct) =>
        {
            if (current.UserId is not Guid userId)
            {
                return Results.BadRequest("No authenticated user.");
            }

            if (string.IsNullOrWhiteSpace(body.InstallationId) || string.IsNullOrWhiteSpace(body.PushToken))
            {
                return Results.BadRequest("installationId and pushToken are both required.");
            }

            if (!Enum.TryParse<DevicePlatform>(body.Platform, ignoreCase: true, out var platform))
            {
                return Results.BadRequest(
                    $"Unknown platform '{body.Platform}'. Expected one of: {string.Join(", ", Enum.GetNames<DevicePlatform>())}.");
            }

            var now = DateTimeOffset.UtcNow;
            var device = await db.UserDevices.FirstOrDefaultAsync(
                d => d.UserId == userId && d.InstallationId == body.InstallationId, ct);

            if (device is null)
            {
                // A generous per-user cap, so a looping client can't grow the table without bound.
                // Past it, the installation that hasn't been seen in longest makes way.
                var existing = await db.UserDevices
                    .Where(d => d.UserId == userId)
                    .OrderBy(d => d.LastSeenAt)
                    .ToListAsync(ct);
                var overflow = existing.Count - push.Value.MaxDevicesPerUser + 1;
                if (overflow > 0)
                {
                    db.UserDevices.RemoveRange(existing.Take(overflow));
                }

                device = new UserDevice
                {
                    TenantId = tenant.RequireTenantId(),
                    UserId = userId,
                    InstallationId = body.InstallationId,
                    PushToken = body.PushToken,
                };
                db.UserDevices.Add(device);
            }

            device.PushToken = body.PushToken;
            device.Platform = platform;
            device.DeviceName = body.DeviceName;
            device.LastSeenAt = now;
            await db.SaveChangesAsync(ct);

            // The token is deliberately absent from the response: it went one way, and a device
            // identifier is not something an endpoint should hand back.
            return Results.Ok(new DeviceDto(device.Id, device.InstallationId, device.Platform.ToString(),
                device.DeviceName, device.LastSeenAt));
        })
        .WithName("Notifications_RegisterDevice");

        // "Which of my devices get notifications?" — for a settings screen, and for noticing an
        // installation you don't recognise. Never includes the tokens themselves.
        group.MapGet("/devices", async (PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            if (current.UserId is not Guid userId)
            {
                return Results.BadRequest("No authenticated user.");
            }

            var devices = await db.UserDevices
                .Where(d => d.UserId == userId)
                .OrderByDescending(d => d.LastSeenAt)
                .Select(d => new DeviceDto(d.Id, d.InstallationId, d.Platform.ToString(), d.DeviceName, d.LastSeenAt))
                .ToListAsync(ct);
            return Results.Ok(devices);
        })
        .WithName("Notifications_ListDevices");

        // Sign-out, or "stop notifying this phone". The row is deleted rather than flagged: once a
        // device should not be reached, keeping its token serves nobody.
        group.MapDelete("/devices/{installationId}", async (
            string installationId, PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            var device = await db.UserDevices.FirstOrDefaultAsync(
                d => d.UserId == current.UserId && d.InstallationId == installationId, ct);
            if (device is null)
            {
                return Results.NotFound();
            }

            db.UserDevices.Remove(device);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .WithName("Notifications_ForgetDevice");
    }

    /// <param name="InstallationId">Stable per-installation id the client mints and keeps.</param>
    /// <param name="PushToken">The push service token to deliver to.</param>
    /// <param name="Platform">"Ios", "Android", or "Web".</param>
    /// <param name="DeviceName">Optional human-readable label for a "your devices" list.</param>
    public sealed record DeviceRegistration(
        string InstallationId, string PushToken, string Platform, string? DeviceName);

    private sealed record DeviceDto(
        Guid Id, string InstallationId, string Platform, string? DeviceName, DateTimeOffset LastSeenAt);

    private sealed record PreferenceDto(
        string Id, string Label, string? Description, string ModuleId, bool Enabled);

    public sealed record PreferenceUpdate(bool Enabled);

    private sealed record NotificationDto(
        Guid Id, string Category, string Title, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
}

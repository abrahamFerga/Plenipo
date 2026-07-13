using Plenipo.Application.Modules;
using Plenipo.Core.Identity;
using Plenipo.Core.Multitenancy;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

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

    private sealed record PreferenceDto(
        string Id, string Label, string? Description, string ModuleId, bool Enabled);

    public sealed record PreferenceUpdate(bool Enabled);

    private sealed record NotificationDto(
        Guid Id, string Category, string Title, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
}

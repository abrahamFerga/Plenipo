using Cortex.Core.Identity;
using Cortex.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Cortex.AspNetCore.Endpoints;

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
    }

    private sealed record NotificationDto(
        Guid Id, string Category, string Title, string Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
}

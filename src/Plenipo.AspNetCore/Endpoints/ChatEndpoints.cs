using Plenipo.Application.Agents;
using Plenipo.Application.Authorization;
using Plenipo.AspNetCore.RateLimiting;
using Plenipo.Core.Identity;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.AspNetCore.Endpoints;

/// <summary>
/// HTTP chat surface (a non-WebSocket alternative to <c>AgentHub</c>): a streaming turn endpoint plus
/// conversation history. Both go through the same authorized, audited agent runner.
/// </summary>
public static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat").WithTags("Chat").RequireAuthorization();

        group.MapPost("/stream", (
                AgentRunRequest request,
                IAuthorizedAgentRunner runner,
                CancellationToken cancellationToken) => runner.RunAsync(request, cancellationToken))
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.UseChat))
            .RequireRateLimiting(RateLimitingSetup.ChatPolicy)
            .WithName("Chat_Stream");

        // The caller's own conversations (most recently updated first), optionally filtered to one module —
        // the data behind the chat history sidebar.
        group.MapGet("/conversations", async (
                PlatformDbContext db, ICurrentUser current, string? moduleId, CancellationToken cancellationToken) =>
            {
                var conversations = await db.Conversations
                    .Where(c => c.UserId == current.UserId && (moduleId == null || c.ModuleId == moduleId))
                    .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt)
                    .Take(100)
                    .Select(c => new ConversationDto(c.Id, c.ModuleId, c.Title, c.UpdatedAt ?? c.CreatedAt))
                    .ToListAsync(cancellationToken);
                return Results.Ok(conversations);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ViewConversations))
            .WithName("Chat_GetConversations");

        // A conversation's message history, for rendering when the user resumes it. Scoped to the caller's
        // own conversations (404 otherwise), so one user can't read another's history.
        group.MapGet("/conversations/{id:guid}/messages", async (
                Guid id, PlatformDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var owned = await db.Conversations
                    .AnyAsync(c => c.Id == id && c.UserId == current.UserId, cancellationToken);
                if (!owned)
                {
                    return Results.NotFound();
                }

                var messages = await db.ConversationMessages
                    .Where(m => m.ConversationId == id)
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => new MessageDto(m.Id, m.Role, m.Content))
                    .ToListAsync(cancellationToken);
                return Results.Ok(messages);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ViewConversations))
            .WithName("Chat_GetConversationMessages");

        // Rename one of the caller's own conversations. Owner-scoped (404 otherwise); auto-titled from the
        // first message until renamed. The change is audited as an entity change by the AuditInterceptor.
        group.MapPut("/conversations/{id:guid}/title", async (
                Guid id, RenameConversationRequest body, PlatformDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var title = body.Title?.Trim();
                if (string.IsNullOrEmpty(title))
                {
                    return Results.BadRequest("A title is required.");
                }

                var conversation = await db.Conversations
                    .FirstOrDefaultAsync(c => c.Id == id && c.UserId == current.UserId, cancellationToken);
                if (conversation is null)
                {
                    return Results.NotFound();
                }

                conversation.Title = title.Length <= 200 ? title : title[..200];
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ViewConversations))
            .WithName("Chat_RenameConversation");

        // Delete one of the caller's own conversations (its messages cascade). 404 for anyone else's.
        group.MapDelete("/conversations/{id:guid}", async (
                Guid id, PlatformDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var conversation = await db.Conversations
                    .FirstOrDefaultAsync(c => c.Id == id && c.UserId == current.UserId, cancellationToken);
                if (conversation is null)
                {
                    return Results.NotFound();
                }

                db.Conversations.Remove(conversation);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ViewConversations))
            .WithName("Chat_DeleteConversation");
    }

    private sealed record ConversationDto(Guid Id, string ModuleId, string? Title, DateTimeOffset UpdatedAt);

    private sealed record MessageDto(Guid Id, MessageRole Role, string Content);

    private sealed record RenameConversationRequest(string? Title);
}

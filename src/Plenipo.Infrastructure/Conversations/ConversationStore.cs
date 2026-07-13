using Plenipo.Application.Conversations;
using Plenipo.Core.Identity;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Infrastructure.Conversations;

/// <summary>
/// EF Core-backed conversation persistence. All reads/writes are automatically tenant-scoped by the
/// <see cref="PlatformDbContext"/> global query filter.
/// </summary>
public sealed class ConversationStore(PlatformDbContext db, ICurrentUser currentUser) : IConversationStore
{
    public async Task<Conversation?> FindAsync(Guid conversationId, CancellationToken cancellationToken = default) =>
        await db.Conversations
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

    public async Task<Conversation> CreateAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        var conversation = new Conversation
        {
            TenantId = currentUser.TenantId ?? throw new InvalidOperationException("Cannot create a conversation without a tenant."),
            UserId = currentUser.UserId ?? throw new InvalidOperationException("Cannot create a conversation without a user."),
            ModuleId = moduleId,
        };

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<Conversation> GetOrCreateAsync(Guid conversationId, string moduleId, CancellationToken cancellationToken = default)
    {
        var existing = await FindAsync(conversationId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var conversation = new Conversation
        {
            Id = conversationId,
            TenantId = currentUser.TenantId ?? throw new InvalidOperationException("Cannot create a conversation without a tenant."),
            UserId = currentUser.UserId ?? throw new InvalidOperationException("Cannot create a conversation without a user."),
            ModuleId = moduleId,
        };

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task AppendTurnAsync(
        Guid conversationId,
        string userMessage,
        string assistantMessage,
        string? sessionState,
        string? instructionsHash = null,
        CancellationToken cancellationToken = default)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            ?? throw new InvalidOperationException($"Conversation {conversationId} was not found.");

        conversation.SessionState = sessionState;
        conversation.Title ??= ConversationTitle.Derive(userMessage);

        db.ConversationMessages.Add(new ConversationMessage
        {
            TenantId = conversation.TenantId,
            ConversationId = conversationId,
            Role = MessageRole.User,
            Content = userMessage,
        });
        db.ConversationMessages.Add(new ConversationMessage
        {
            TenantId = conversation.TenantId,
            ConversationId = conversationId,
            Role = MessageRole.Assistant,
            Content = assistantMessage,
            InstructionsHash = instructionsHash,
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}

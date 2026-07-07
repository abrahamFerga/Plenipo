using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Cortex.Application.Agents;
using Cortex.Application.Auditing;
using Cortex.Application.Authorization;
using Cortex.Application.Channels;
using Cortex.Application.Files;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Context;
using Cortex.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cortex.Infrastructure.Channels;

/// <summary>
/// The one implementation of the inbound-channel core (docs/INBOUND_CHANNELS.md), extracted from the
/// WhatsApp lane. Identity mirrors the HTTP pipeline's RequestEnricher — resolve tenant →
/// JIT-provision the sender (subject <c>{channelId}:{externalId}</c>, role <c>user</c>) → resolve
/// permissions — so every channel turn runs with exactly the authority of that user.
/// </summary>
public sealed class ChannelTurnService(
    RequestContext requestContext,
    PlatformDbContext db,
    IPermissionResolver permissionResolver,
    IAuthorizedAgentRunner runner,
    IFileStore files,
    IAuditLog auditLog,
    ILogger<ChannelTurnService> logger) : IChannelTurnService
{
    public async Task<ChannelTurnResult> RunAsync(ChannelTurnRequest request, CancellationToken cancellationToken = default)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == request.TenantSlug, cancellationToken);
        if (tenant is null || !tenant.IsActive)
        {
            logger.LogError(
                "Channel '{ChannelId}' is bound to tenant '{Slug}' which {Reason}; dropping message.",
                request.ChannelId, request.TenantSlug, tenant is null ? "does not exist" : "is deactivated");
            return new(ChannelTurnStatus.TenantUnavailable);
        }

        // Same population order as the HTTP pipeline: tenant first, so every query below is tenant-scoped.
        requestContext.SetTenant(tenant.Id);

        var subject = $"{request.ChannelId}:{request.ExternalId}";
        var user = await db.Users.FirstOrDefaultAsync(u => u.Subject == subject, cancellationToken);
        if (user is { IsActive: false })
        {
            logger.LogWarning("Deactivated user {Subject} messaged channel '{ChannelId}'; dropping.", subject, request.ChannelId);
            return new(ChannelTurnStatus.IdentityRefused);
        }

        if (user is null)
        {
            // Same seat gate as interactive sign-in: a full tenant admits no new channel users.
            if (tenant.MaxSeats is { } maxSeats)
            {
                var activeSeats = await db.Users.CountAsync(u => u.IsActive, cancellationToken);
                if (activeSeats >= maxSeats)
                {
                    logger.LogWarning("Channel user {Subject} refused: tenant seat limit {MaxSeats} reached.", subject, maxSeats);
                    return new(ChannelTurnStatus.IdentityRefused);
                }
            }

            var displayName = request.DisplayName ?? subject;
            user = new User
            {
                TenantId = tenant.Id,
                Subject = subject,
                Email = $"{request.ExternalId}@{request.ChannelId}.channel",
                DisplayName = displayName,
            };
            user.Roles.Add(new UserRole { TenantId = tenant.Id, UserId = user.Id, Role = Roles.User });
            db.Users.Add(user);
            await db.SaveChangesAsync(cancellationToken);

            await auditLog.RecordAuthEventAsync(new AuthAuditEntry
            {
                TenantId = tenant.Id,
                UserId = user.Id,
                Subject = subject,
                UserDisplay = displayName,
                EventType = AuthAuditEventType.UserProvisioned,
            }, cancellationToken);
        }
        else
        {
            user.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        requestContext.SetUser(user.Id, subject, user.DisplayName);

        // No token exists for a channel caller; permissions come entirely from the user's DB roles/grants.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], request.ChannelId));
        requestContext.SetPermissions(await permissionResolver.ResolveAsync(principal, cancellationToken));

        if (!requestContext.HasPermission(Permissions.UseChat))
        {
            return new(ChannelTurnStatus.NoChatAccess);
        }

        // Attachments become stored files plus the same plain-text reference block the web composer
        // uses — so the agent's document tools (read_document, ocr_document) work identically here.
        var text = request.Text;
        if (request.Attachments.Count > 0)
        {
            var composed = new StringBuilder(
                string.IsNullOrWhiteSpace(text) ? "Please look at the attached file(s)." : text);
            composed.Append("\n\n[Attached files]");
            foreach (var attachment in request.Attachments)
            {
                await using var content = attachment.Content;
                var stored = await files.SaveAsync(
                    attachment.FileName, attachment.ContentType, content,
                    source: request.ChannelId, cancellationToken);
                composed.Append($"\n- {stored.FileName} (file id: {stored.Id})");
            }

            text = composed.ToString();
        }

        var runRequest = new AgentRunRequest
        {
            ModuleId = request.ModuleId,
            // One long-running conversation per sender, tenant-scoped — the same stable-id scheme
            // the AG-UI endpoint uses for client-owned thread ids.
            ConversationId = ConversationIdFor(tenant.Id, subject),
            Message = text,
        };

        var reply = new StringBuilder();
        var approvals = new List<string>();

        await foreach (var evt in runner.RunAsync(runRequest, cancellationToken))
        {
            switch (evt.Type)
            {
                case AgentStreamEventType.Token when !string.IsNullOrEmpty(evt.Text):
                    reply.Append(evt.Text);
                    break;

                case AgentStreamEventType.ApprovalRequired when evt.ToolName is not null:
                    approvals.Add(evt.ToolName);
                    break;

                case AgentStreamEventType.Error:
                    logger.LogError("Channel '{ChannelId}' agent turn failed: {Error}", request.ChannelId, evt.Error);
                    return new(ChannelTurnStatus.Failed);
            }
        }

        foreach (var tool in approvals)
        {
            reply.Append(reply.Length > 0 ? "\n\n" : string.Empty)
                 .Append("⏳ The action \"").Append(tool)
                 .Append("\" needs approval before it runs. An operator can approve it from the workspace.");
        }

        return new(ChannelTurnStatus.Completed, reply.ToString().Trim());
    }

    /// <summary>Stable, tenant-scoped conversation id for a channel sender, so every message from the
    /// same identity continues one conversation (and two tenants can never collide on one row).</summary>
    public static Guid ConversationIdFor(Guid tenantId, string subject)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N} {subject}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Plenipo.Application.Agents;
using Plenipo.Application.Authorization;
using Plenipo.AspNetCore.RateLimiting;
using Plenipo.Core.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace Plenipo.AspNetCore.Endpoints;

/// <summary>
/// An <a href="https://docs.ag-ui.com/">AG-UI</a>-protocol-compatible chat endpoint. AG-UI is the open
/// agent-to-user-interaction standard (HTTP POST + Server-Sent Events) used by CopilotKit and other open
/// front-ends. Rather than expose the raw MAF agent (which would bypass Plenipo's per-user tool
/// authorization), this endpoint drives the same <see cref="IAuthorizedAgentRunner"/> as the SignalR hub
/// and the REST stream — so RBAC tool-filtering, auditing, and token tracking all still apply — and
/// re-encodes the resulting events as AG-UI SSE frames. Any AG-UI client can therefore talk to Plenipo.
/// </summary>
public static class AguiEndpoints
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapAgui(this IEndpointRouteBuilder app)
    {
        // POST /api/agui/{moduleId} — body is an AG-UI RunAgentInput; response is an SSE event stream.
        app.MapPost("/api/agui/{moduleId}", HandleAsync)
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.UseChat))
            .RequireRateLimiting(RateLimitingSetup.ChatPolicy)
            .WithTags("Chat")
            .WithName("Agui_Run");
    }

    private static async Task HandleAsync(
        string moduleId,
        RunAgentInput input,
        HttpContext http,
        IAuthorizedAgentRunner runner,
        ICurrentUser currentUser,
        Plenipo.Infrastructure.Persistence.PlatformDbContext db,
        CancellationToken cancellationToken)
    {
        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var threadId = string.IsNullOrWhiteSpace(input.ThreadId) ? $"thread_{Guid.CreateVersion7():N}" : input.ThreadId;
        var runId = string.IsNullOrWhiteSpace(input.RunId) ? $"run_{Guid.CreateVersion7():N}" : input.RunId;

        await WriteEventAsync(response, new { type = "RUN_STARTED", threadId, runId }, cancellationToken);

        var userMessage = input.Messages?.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (userMessage is null || string.IsNullOrWhiteSpace(userMessage.Content))
        {
            await WriteEventAsync(response, new { type = "RUN_ERROR", message = "No user message supplied." }, cancellationToken);
            return;
        }

        var request = new AgentRunRequest
        {
            ModuleId = moduleId,
            // Map the AG-UI thread id (which the client owns) to a stable, tenant-scoped conversation id, so a
            // reused thread id continues the same conversation instead of starting a fresh one each turn. A
            // thread id that IS an existing conversation's id resumes that conversation directly — how a client
            // (incl. our own ChatPanel) picks a conversation from history and keeps talking over AG-UI. The
            // existence check is tenant-filtered, so a foreign tenant's id falls through to the hash mapping.
            ConversationId = await ResolveConversationIdAsync(db, currentUser.TenantId ?? Guid.Empty, threadId, cancellationToken),
            Message = userMessage.Content,
            // AG-UI carries client extensions in forwardedProps; ours are the composer's agent and
            // model picks. The runner validates both — unknown names fail the run readably.
            Agent = ReadForwardedProp(input.ForwardedProps, "agent"),
            Model = ReadForwardedProp(input.ForwardedProps, "model"),
        };

        var messageId = $"msg_{Guid.CreateVersion7():N}";
        var textStarted = false;

        await foreach (var evt in runner.RunAsync(request, cancellationToken))
        {
            switch (evt.Type)
            {
                case AgentStreamEventType.Token when !string.IsNullOrEmpty(evt.Text):
                    if (!textStarted)
                    {
                        textStarted = true;
                        await WriteEventAsync(response, new { type = "TEXT_MESSAGE_START", messageId, role = "assistant" }, cancellationToken);
                    }
                    await WriteEventAsync(response, new { type = "TEXT_MESSAGE_CONTENT", messageId, delta = evt.Text }, cancellationToken);
                    break;

                case AgentStreamEventType.ToolInvoked when evt.ToolName is not null:
                    var toolCallId = $"tool_{Guid.CreateVersion7():N}";
                    await WriteEventAsync(response, new { type = "TOOL_CALL_START", toolCallId, toolCallName = evt.ToolName }, cancellationToken);
                    await WriteEventAsync(response, new { type = "TOOL_CALL_END", toolCallId }, cancellationToken);
                    break;

                case AgentStreamEventType.Usage:
                    await WriteEventAsync(response, new
                    {
                        type = "CUSTOM",
                        name = "token_usage",
                        value = new { inputTokens = evt.InputTokens, outputTokens = evt.OutputTokens, totalTokens = evt.TotalTokens },
                    }, cancellationToken);
                    break;

                case AgentStreamEventType.ApprovalRequired:
                    await WriteEventAsync(response, new
                    {
                        type = "CUSTOM",
                        name = "approval_required",
                        value = new { toolName = evt.ToolName },
                    }, cancellationToken);
                    break;

                case AgentStreamEventType.Completed:
                    if (textStarted)
                    {
                        await WriteEventAsync(response, new { type = "TEXT_MESSAGE_END", messageId }, cancellationToken);
                    }
                    await WriteEventAsync(response, new
                    {
                        type = "RUN_FINISHED",
                        threadId, // echo the client's thread id (it owns it); the internal conversation id is in result
                        runId,
                        result = new { conversationId = evt.ConversationId },
                    }, cancellationToken);
                    break;

                case AgentStreamEventType.Error:
                    await WriteEventAsync(response, new { type = "RUN_ERROR", message = evt.Error ?? "The assistant could not complete the request." }, cancellationToken);
                    return;
            }
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, EventJson);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Maps an AG-UI thread id (any string the client owns) to a stable, tenant-scoped conversation id, so a
    /// reused thread id continues the same conversation. Tenant-scoped so the same thread id in two tenants
    /// can never collide on one conversation row.
    /// </summary>
    private static async Task<Guid> ResolveConversationIdAsync(
        Plenipo.Infrastructure.Persistence.PlatformDbContext db, Guid tenantId, string threadId,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(threadId, out var conversationId) &&
            await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(
                db.Conversations, c => c.Id == conversationId, cancellationToken))
        {
            return conversationId;
        }

        return ConversationIdForThread(tenantId, threadId);
    }

    private static Guid ConversationIdForThread(Guid tenantId, string threadId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:N} {threadId}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>A string property from AG-UI forwardedProps; null when absent, non-object, or not a string.</summary>
    private static string? ReadForwardedProp(JsonElement? props, string name) =>
        props is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The AG-UI <c>RunAgentInput</c> request body (the subset Plenipo consumes).</summary>
    public sealed record RunAgentInput
    {
        public string? ThreadId { get; init; }
        public string? RunId { get; init; }
        public IReadOnlyList<AguiMessage>? Messages { get; init; }
        public JsonElement? State { get; init; }
        public JsonElement? ForwardedProps { get; init; }
    }

    public sealed record AguiMessage
    {
        public string? Id { get; init; }
        public required string Role { get; init; }
        public string? Content { get; init; }
    }
}

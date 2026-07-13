using System.Diagnostics;
using System.Text.Json;
using Plenipo.Application.Auditing;
using Plenipo.Core.Identity;
using Plenipo.Modules.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Plenipo.Infrastructure.Agents;

/// <summary>A side-effecting tool call the agent attempted, captured (with its arguments) for approval.</summary>
public sealed record BlockedToolCall(string ToolName, string? ArgumentsJson);

/// <summary>
/// Function-invocation middleware that audits every agent tool call and enforces human-in-the-loop
/// approval. A tool whose manifest descriptor sets <see cref="ToolDescriptor.RequiresApproval"/> is a
/// side-effecting action: this middleware <em>refuses to execute it</em>, records the blocked attempt,
/// and returns a message telling the model the action is pending the user's approval. Read-only tools
/// run normally and are audited around execution. The runner surfaces the blocked tools to the client.
/// </summary>
public sealed class ToolInvocationMiddleware(
    IAuditLog auditLog,
    ICurrentUser currentUser,
    IReadOnlySet<string> approvalRequiredTools,
    IReadOnlyDictionary<string, ModuleTool> toolsByName,
    string moduleId,
    Guid conversationId)
{
    /// <summary>
    /// The audit-entry error recorded when a side-effecting tool call is blocked pending approval. A
    /// blocked call is NOT an execution — it lives on as a <c>PendingApproval</c> whose resolution is
    /// the real record — so readers that must not double-count (the ADMT disclosure view) filter
    /// audit rows carrying exactly this marker.
    /// </summary>
    public const string ApprovalBlockedError = "Blocked: tool requires human approval";

    private readonly List<BlockedToolCall> _blockedForApproval = [];

    /// <summary>Side-effecting tool calls the agent attempted this turn that were blocked pending approval.</summary>
    public IReadOnlyList<BlockedToolCall> BlockedForApproval => _blockedForApproval;

    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var name = context.Function.Name;
        toolsByName.TryGetValue(name, out var moduleTool);

        // Deny-by-default for side-effecting tools: never auto-execute without human approval.
        if (approvalRequiredTools.Contains(name))
        {
            _blockedForApproval.Add(new BlockedToolCall(name, SafeSerializeArguments(context.Arguments)));
            await RecordAsync(name, moduleTool, context.Arguments, success: false,
                error: ApprovalBlockedError, durationMs: 0, cancellationToken);

            return $"The action '{name}' requires human approval before it can run, so it was NOT executed. " +
                   "Tell the user this action is pending their approval and do not claim it was completed.";
        }

        var stopwatch = Stopwatch.StartNew();
        var success = true;
        string? failure = null;
        try
        {
            return await next(context, cancellationToken);
        }
        catch (Exception ex)
        {
            success = false;
            failure = ex.Message;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            await RecordAsync(name, moduleTool, context.Arguments, success, failure, stopwatch.ElapsedMilliseconds, cancellationToken);
        }
    }

    private Task RecordAsync(
        string toolName,
        ModuleTool? moduleTool,
        AIFunctionArguments? arguments,
        bool success,
        string? error,
        long durationMs,
        CancellationToken cancellationToken) =>
        auditLog.RecordToolCallAsync(new ToolCallAuditEntry
        {
            TenantId = currentUser.TenantId ?? Guid.Empty,
            UserId = currentUser.UserId,
            UserDisplay = currentUser.DisplayName,
            ModuleId = moduleId,
            ToolName = toolName,
            Permission = moduleTool?.Permission ?? string.Empty,
            ArgumentsJson = SafeSerializeArguments(arguments),
            ConversationId = conversationId,
            Success = success,
            Error = error,
            DurationMs = durationMs,
        }, cancellationToken);

    private static string? SafeSerializeArguments(AIFunctionArguments? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value));
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

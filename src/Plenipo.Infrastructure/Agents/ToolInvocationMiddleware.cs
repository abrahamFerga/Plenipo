using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Plenipo.Application.Ai;
using Plenipo.Application.Auditing;
using Plenipo.Core.Identity;
using Plenipo.Infrastructure.Agents.Security;
using Plenipo.Modules.Sdk;

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
    Guid conversationId,
    IAgentSecurityService? agentSecurity = null,
    EffectiveAgentSecurityPolicy? securityPolicy = null)
{
    /// <summary>
    /// The audit-entry error recorded when a side-effecting tool call is blocked pending approval. A
    /// blocked call is NOT an execution — it lives on as a <c>PendingApproval</c> whose resolution is
    /// the real record — so readers that must not double-count (the ADMT disclosure view) filter
    /// audit rows carrying exactly this marker.
    /// </summary>
    public const string ApprovalBlockedError = "Blocked: tool requires human approval";
    public const string SecurityBlockedError = "Blocked: agent security policy";

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

        // The model is untrusted even when its proposed tool call looks structurally valid. Inspect the
        // arguments before approval or execution; in Redact mode, mutate string arguments so a tool never
        // receives the sensitive value.
        if (agentSecurity is not null && securityPolicy is { IsEnabled: true })
        {
            var serializedArguments = context.Arguments is null
                ? "{}"
                : SerializeArguments(context.Arguments, redactForAudit: false);
            var inspection = serializedArguments is null
                ? Uninspectable(securityPolicy, "UninspectableToolInput")
                : await agentSecurity.InspectAsync(
                    serializedArguments,
                    AgentSecurityStage.ToolInput,
                    securityPolicy,
                    cancellationToken);
            await RecordSecurityAsync(AgentSecurityStage.ToolInput, inspection, cancellationToken);

            if (inspection.Blocked)
            {
                await RecordAsync(name, moduleTool, context.Arguments, success: false,
                    error: SecurityBlockedError, durationMs: 0, cancellationToken);
                return SecurityRefusal(inspection.Unavailable);
            }

            if (inspection.Modified)
            {
                SensitiveDataDetector.RedactArguments(context.Arguments);
            }
        }

        // Deny-by-default for side-effecting tools: never auto-execute without human approval.
        if (approvalRequiredTools.Contains(name))
        {
            _blockedForApproval.Add(new BlockedToolCall(
                name,
                SerializeArguments(context.Arguments, redactForAudit: false)));
            await RecordAsync(name, moduleTool, context.Arguments, success: false,
                error: ApprovalBlockedError, durationMs: 0, cancellationToken);

            return $"The action '{name}' requires human approval before it can run, so it was NOT executed. " +
                   "Tell the user this action is pending their approval and do not claim it was completed. " +
                   "When a human approves or rejects it, the outcome will arrive in a later turn as an " +
                   "'[Approval outcomes]' note — that note supersedes this message.";
        }

        var stopwatch = Stopwatch.StartNew();
        var success = true;
        string? failure = null;
        try
        {
            var result = await next(context, cancellationToken);
            if (agentSecurity is null || securityPolicy is not { IsEnabled: true })
            {
                return result;
            }

            var serializedResult = SerializeResult(result);
            if (serializedResult is null)
            {
                var unavailable = Uninspectable(securityPolicy, "UninspectableToolOutput");
                await RecordSecurityAsync(AgentSecurityStage.ToolOutput, unavailable, cancellationToken);
                if (unavailable.Blocked)
                {
                    return SecurityRefusal(unavailable: true);
                }

                return result;
            }

            // Tool responses are untrusted retrieved content. Prompt Shields treats them as documents so
            // indirect instructions are detected before they enter the model's next context.
            var inspection = await agentSecurity.InspectAsync(
                serializedResult,
                AgentSecurityStage.ToolOutput,
                securityPolicy,
                cancellationToken);
            await RecordSecurityAsync(AgentSecurityStage.ToolOutput, inspection, cancellationToken);

            if (inspection.Blocked)
            {
                return SecurityRefusal(inspection.Unavailable);
            }

            return inspection.Modified ? inspection.Text : result;
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
            // Audit records never contain recognizable PII or credentials, even when tenant enforcement is off.
            ArgumentsJson = SerializeArguments(arguments, redactForAudit: true),
            ConversationId = conversationId,
            Success = success,
            Error = error,
            DurationMs = durationMs,
        }, cancellationToken);

    private Task RecordSecurityAsync(
        AgentSecurityStage stage,
        AgentSecurityInspection inspection,
        CancellationToken cancellationToken)
    {
        if (!inspection.HasFindings)
        {
            return Task.CompletedTask;
        }

        var eventType = inspection.Unavailable
            ? AuthAuditEventType.AgentSecurityUnavailable
            : inspection.Blocked
                ? AuthAuditEventType.AgentSecurityBlocked
                : AuthAuditEventType.AgentSecurityDetected;
        var findingNames = inspection.Findings
            .Select(f => $"{f.Detector}:{f.Category}")
            .Distinct(StringComparer.Ordinal);

        return auditLog.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = currentUser.TenantId,
            UserId = currentUser.UserId,
            Subject = currentUser.Subject,
            UserDisplay = currentUser.DisplayName,
            EventType = eventType,
            Detail = $"stage={stage}; findings={string.Join(",", findingNames)}",
        }, cancellationToken);
    }

    private static string SecurityRefusal(bool unavailable) => unavailable
        ? "The tool call was not executed because required security screening is temporarily unavailable."
        : "The tool call was not executed because it violates the configured agent security policy.";

    private static AgentSecurityInspection Uninspectable(
        EffectiveAgentSecurityPolicy policy,
        string category) =>
        new()
        {
            Text = string.Empty,
            Unavailable = true,
            Blocked = policy.Mode == AgentSecurityMode.Enforce && policy.FailClosed,
            Findings = [new AgentSecurityFinding("Policy", category)],
        };

    private static string? SerializeArguments(AIFunctionArguments? arguments, bool redactForAudit)
    {
        if (arguments is null)
        {
            return null;
        }

        try
        {
            var serialized = JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value));
            return redactForAudit ? SensitiveDataDetector.RedactSerialized(serialized) : serialized;
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return null;
        }
    }

    private static string? SerializeResult(object? result)
    {
        if (result is null)
        {
            return "null";
        }

        if (result is string text)
        {
            return text;
        }

        try
        {
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            // A non-serializable result cannot be inspected as text; the framework will handle it as before.
            return null;
        }
    }
}

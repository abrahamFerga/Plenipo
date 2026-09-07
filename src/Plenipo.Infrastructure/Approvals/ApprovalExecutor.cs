using System.Text.Json;
using Plenipo.Application.Agents;
using Plenipo.Application.Connectors;
using Plenipo.Core.Platform;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;

namespace Plenipo.Infrastructure.Approvals;

/// <summary>The outcome of executing an approved tool call.</summary>
public readonly record struct ApprovalExecutionResult(bool Success, string? Result, string? Error);

/// <summary>
/// Re-executes an approved, side-effecting tool call with its recorded arguments. Resolves the tool from
/// the module's registered tool source within the given scope (so the tool's scoped services — its
/// DbContext, the current tenant and user — are wired), falling back to the tenant's enabled connector
/// tools (which the runner offers alongside module tools), then invokes it. Argument coercion from the
/// stored JSON back into the tool's typed parameters is handled by the <see cref="AIFunction"/> itself.
/// <para>
/// This is the mechanical half. <see cref="ApprovalRelease"/> is what the approve endpoint calls: it
/// builds the scope this runs in — the <em>requester's</em> identity and snapshotted authority, never the
/// approver's — and writes the execution and the decision to the audit trail.
/// </para>
/// </summary>
public sealed class ApprovalExecutor(IToolRegistry toolRegistry, IConnectorToolCatalog connectorTools)
{
    /// <summary>
    /// The executable behind a parked call, resolved in <paramref name="scopedServices"/>: the module's
    /// tool of that name, else an enabled connector tool of that name, else null when the tool is no
    /// longer available (module uninstalled, connector disabled since the block).
    /// </summary>
    public async Task<ModuleTool?> ResolveToolAsync(
        PendingApproval approval, IServiceProvider scopedServices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);

        return toolRegistry.GetModuleTools(approval.ModuleId, scopedServices)
                .FirstOrDefault(t => string.Equals(t.Name, approval.ToolName, StringComparison.Ordinal))
            ?? (await connectorTools.GetEnabledToolsAsync(scopedServices, cancellationToken))
                .FirstOrDefault(t => string.Equals(t.Name, approval.ToolName, StringComparison.Ordinal));
    }

    public async Task<ApprovalExecutionResult> ExecuteAsync(
        PendingApproval approval, IServiceProvider scopedServices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);

        var tool = await ResolveToolAsync(approval, scopedServices, cancellationToken);
        return tool is null
            ? new ApprovalExecutionResult(false, null, $"Tool '{approval.ToolName}' is no longer available.")
            : await InvokeAsync(tool, approval, cancellationToken);
    }

    /// <summary>Invokes an already-resolved tool with the approval's recorded arguments.</summary>
    public static async Task<ApprovalExecutionResult> InvokeAsync(
        ModuleTool tool, PendingApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(approval);

        try
        {
            var arguments = DeserializeArguments(approval.ArgumentsJson);
            var result = await tool.Function.InvokeAsync(arguments, cancellationToken);
            return new ApprovalExecutionResult(true, result?.ToString(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ApprovalExecutionResult(false, null, ex.Message);
        }
    }

    private static AIFunctionArguments DeserializeArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AIFunctionArguments();
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        return new AIFunctionArguments(parsed.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
    }
}

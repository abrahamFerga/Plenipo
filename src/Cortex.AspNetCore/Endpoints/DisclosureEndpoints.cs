using System.Text.Json;
using Cortex.Application.Agents;
using Cortex.Application.Auditing;
using Cortex.Application.Connectors;
using Cortex.Application.Modules;
using Cortex.Core.Identity;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Agents;
using Cortex.Infrastructure.Persistence;
using Cortex.Modules.Sdk;
using Microsoft.EntityFrameworkCore;

namespace Cortex.AspNetCore.Endpoints;

/// <summary>
/// The consumer-facing automated-decision (ADMT) disclosure surface: the caller's tenant's
/// AI-decision history in plain language — what the agent did or proposed, when, on what basis,
/// and with what human oversight. California's CPPA ADMT rules (and general AI-transparency
/// posture) expect a product to answer exactly this, so it is a platform read, not an admin one:
/// any authenticated member of the tenant may see their own tenant's history (contrast
/// <c>/api/admin/audit/*</c>, which is gated on <c>platform.audit.view</c>). Two stores together
/// hold the full story and are merged here:
/// <list type="bullet">
/// <item><c>PendingApproval</c> rows (platform DB, tenant-scoped by the global query filter) are
/// the record of GATED actions — each resolution is a human decision: approved by whom, or
/// rejected. Blocked-then-decided is the only life a gated call has; its re-execution happens
/// outside the agent middleware, so the resolved row is authoritative and unique.</item>
/// <item><c>ToolCallAuditEntry</c> rows (audit DB — no query filter, scoped explicitly) are the
/// record of UNGATED executions: tools that ran automatically because their declaration does not
/// require approval. Rows carrying <see cref="ToolInvocationMiddleware.ApprovalBlockedError"/>
/// are excluded — they are the audit shadow of a pending approval, and counting both would
/// double-report the same action.</item>
/// </list>
/// </summary>
public static class DisclosureEndpoints
{
    public static void MapDisclosureEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform/ai-decisions").WithTags("Disclosure").RequireAuthorization();

        // Windowed, recent-first: `take` is the house pagination convention (capped, no envelope);
        // `before` lets a caller page back through history — fetch, then re-query with the oldest
        // occurredAt received. The frontend's export uses the same read, so what a user downloads
        // is exactly what they were shown.
        group.MapGet("/", async (
            PlatformDbContext db,
            AuditDbContext audit,
            ICurrentUser current,
            IToolRegistry toolRegistry,
            IConnectorToolCatalog connectorTools,
            IModuleCatalog modules,
            IServiceProvider services,
            int? take,
            DateTimeOffset? before,
            CancellationToken ct) =>
        {
            var limit = Math.Clamp(take ?? 100, 1, 500);
            var tenantId = current.TenantId ?? Guid.Empty;

            // Gated actions that reached a human decision. Pending ones are not yet decisions —
            // they live on the approvals queue, not in the disclosure. The decision instant
            // (ResolvedAt) is the record's "when"; CreatedAt only backstops legacy rows.
            var decided = await db.PendingApprovals
                .Where(p => p.Status != ApprovalStatus.Pending)
                .Where(p => before == null || (p.ResolvedAt ?? p.CreatedAt) < before)
                .OrderByDescending(p => p.ResolvedAt ?? p.CreatedAt)
                .Take(limit)
                .ToListAsync(ct);

            // Ungated executions (the audit store has no query filter — scope explicitly). The
            // null-check is deliberate: successful rows have no error, and they must be included.
            var automatic = await audit.ToolCalls
                .Where(t => t.TenantId == tenantId)
                .Where(t => t.Error == null || t.Error != ToolInvocationMiddleware.ApprovalBlockedError)
                .Where(t => before == null || t.OccurredAt < before)
                .OrderByDescending(t => t.OccurredAt)
                .Take(limit)
                .ToListAsync(ct);

            // Enrich from each record's DECLARING tool (human label + risk tier), the same
            // read-time resolution ApprovalEndpoints uses: the declaration is the living source
            // of truth, and an unresolvable tool (module uninstalled since) degrades to the raw
            // tool name and fails safe to high risk.
            var connector = decided.Count > 0 || automatic.Count > 0
                ? await connectorTools.GetEnabledToolsAsync(services, ct)
                : [];
            var byModule = new Dictionary<string, IReadOnlyList<ModuleTool>>(StringComparer.Ordinal);
            ModuleTool? Resolve(string moduleId, string toolName)
            {
                if (!byModule.TryGetValue(moduleId, out var tools))
                {
                    tools = toolRegistry.GetModuleTools(moduleId, services);
                    byModule[moduleId] = tools;
                }

                return tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal))
                    ?? connector.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
            }

            string? ModuleName(string moduleId) =>
                modules.TryGetManifest(moduleId, out var manifest) ? manifest?.DisplayName : null;

            var entries = decided.Select(p => ToDto(p, Resolve(p.ModuleId, p.ToolName), ModuleName(p.ModuleId)))
                .Concat(automatic.Select(t => ToDto(t, Resolve(t.ModuleId, t.ToolName), ModuleName(t.ModuleId))))
                .OrderByDescending(e => e.OccurredAt)
                .Take(limit)
                .ToArray();

            return Results.Ok(entries);
        })
        .WithName("Disclosure_ListAiDecisions");
    }

    private static AiDecisionDto ToDto(PendingApproval p, ModuleTool? tool, string? moduleName)
    {
        var (argsSummary, reasoning) = ParseArguments(p.ArgumentsJson);
        return new AiDecisionDto(
            p.Id,
            "approval",
            p.ResolvedAt ?? p.CreatedAt,
            p.ModuleId,
            moduleName,
            p.ToolName,
            ToolDescription(tool),
            BuildSummary(p.ToolName, tool, argsSummary),
            reasoning,
            // Rejected is the one human "no"; Executed AND Failed both mean a human said yes —
            // a failed execution is still an approved decision, with the failure carried in Error.
            p.Status == ApprovalStatus.Rejected ? "rejected" : "approved",
            // Same wire contract and fail-safe as the approvals queue: unresolvable → "high".
            tool?.Risk == ApprovalRisk.Low ? "low" : "high",
            p.UserDisplay,
            p.ResolvedByDisplay,
            p.ResolvedAt,
            p.ConversationId,
            p.Error);
    }

    private static AiDecisionDto ToDto(ToolCallAuditEntry t, ModuleTool? tool, string? moduleName)
    {
        var (argsSummary, reasoning) = ParseArguments(t.ArgumentsJson);
        return new AiDecisionDto(
            t.Id,
            "audit",
            t.OccurredAt,
            t.ModuleId,
            moduleName,
            t.ToolName,
            ToolDescription(tool),
            BuildSummary(t.ToolName, tool, argsSummary),
            reasoning,
            "automatic",
            // Risk is review-surface ceremony for GATED tools; an ungated execution has no review
            // tier to report, and pretending "high" here would read as alarm, not information.
            Risk: null,
            t.UserDisplay,
            DecidedBy: null,
            DecidedAt: null,
            t.ConversationId,
            t.Error);
    }

    private static string? ToolDescription(ModuleTool? tool) =>
        tool?.Function.Description is { Length: > 0 } d ? d : null;

    /// <summary>The one-line plain-language account: the tool's human description (or its name,
    /// de-mangled) plus a compact rendering of the recorded arguments — what the disclosure calls
    /// "what changed".</summary>
    private static string BuildSummary(string toolName, ModuleTool? tool, string? argsSummary)
    {
        var label = ToolDescription(tool) ?? Humanize(toolName);
        return argsSummary is null ? label : $"{label} — {argsSummary}";
    }

    /// <summary>"finance.update_budget" → "finance update budget": readable without pretending to
    /// be a sentence, and never fabricating meaning the declaration didn't provide.</summary>
    private static string Humanize(string toolName) =>
        toolName.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ').Trim();

    /// <summary>
    /// One pass over the recorded arguments: a compact "key: value" summary of the top-level
    /// fields, plus the agent's stated <c>reasoning</c> lifted out separately — the same argument
    /// convention the approvals card renders, surfaced here as the decision's "basis".
    /// </summary>
    private static (string? ArgsSummary, string? Reasoning) ParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? reasoning = null;
            var parts = new List<string>();
            var more = 0;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.NameEquals("reasoning") && property.Value.ValueKind == JsonValueKind.String)
                {
                    reasoning = property.Value.GetString() is { Length: > 0 } r ? r : null;
                    continue;
                }

                if (parts.Count >= 4)
                {
                    more++;
                    continue;
                }

                parts.Add($"{property.Name}: {Render(property.Value)}");
            }

            var summary = parts.Count == 0 ? null : string.Join(", ", parts) + (more > 0 ? $", +{more} more" : "");
            return (summary, reasoning);
        }
        catch (JsonException)
        {
            // Unparseable recorded arguments stay in the audit store; the summary just goes without.
            return (null, null);
        }
    }

    private static string Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Truncate(value.GetString() ?? ""),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Null or JsonValueKind.Undefined => "—",
        JsonValueKind.Array => $"[{value.GetArrayLength()} items]",
        _ => "{…}",
    };

    private static string Truncate(string value) =>
        value.Length <= 60 ? value : value[..59] + "…";

    /// <summary>
    /// One AI-originated action in disclosure form. <c>Source</c> says which append-only store the
    /// stable <c>Id</c> lives in ("approval" = the platform's pending-approvals table, "audit" =
    /// the audit database's tool-calls table), so an exported record remains verifiable against
    /// the underlying trail. <c>Oversight</c>/<c>Risk</c> are lowercase string literals on the
    /// wire, the same contract style as the approvals queue.
    /// </summary>
    private sealed record AiDecisionDto(
        Guid Id,
        string Source,
        DateTimeOffset OccurredAt,
        string ModuleId,
        string? ModuleName,
        string ToolName,
        string? ToolDescription,
        string Summary,
        string? Basis,
        string Oversight,
        string? Risk,
        string? RequestedBy,
        string? DecidedBy,
        DateTimeOffset? DecidedAt,
        Guid? ConversationId,
        string? Error);
}

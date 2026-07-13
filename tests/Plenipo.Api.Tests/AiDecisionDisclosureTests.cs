using System.Net;
using System.Net.Http.Json;
using Plenipo.Application.Auditing;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Agents;
using Plenipo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of the ADMT disclosure surface (<c>GET /api/platform/ai-decisions</c>): the
/// consumer-facing account of what the AI did or proposed and what human oversight applied. The
/// contract under test: resolved approvals surface as human decisions WITH attribution (approved by
/// whom), ungated executions surface as "automatic", a blocked call is never double-counted against
/// its approval record, still-pending items are not yet decisions, a plain (non-admin) user may read
/// it, and — as with every tenant-owned read — nothing crosses a tenant boundary.
/// </summary>
public sealed class AiDecisionDisclosureTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public AiDecisionDisclosureTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject, string? name = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        if (name is not null)
        {
            client.DefaultRequestHeaders.Add("X-Dev-Name", name);
        }

        return client;
    }

    private Task<List<AiDecisionDto>> DisclosureAsSelfServeUserAsync(string subject, string query = "") =>
        // Deliberately the PLAIN user role: the disclosure is a self-serve transparency read, not an
        // admin audit view, so no platform.audit.view / chat.approvals.manage is required.
        ClientAs("user", subject).GetFromJsonAsync<List<AiDecisionDto>>($"/api/platform/ai-decisions{query}")!;

    [Fact]
    public async Task An_approved_action_discloses_who_approved_it()
    {
        var id = SeedDevPendingApproval(
            toolName: "record",
            argumentsJson: """{"value":"42","reasoning":"the user asked for it"}""");

        // Approve through the real endpoint so the resolver attribution is captured by the same
        // code path production uses — not painted onto the row by the test.
        var approver = ClientAs("system_admin", "admt-approver", name: "Dana Reviewer");
        (await approver.PostAsync($"/api/chat/approvals/{id}/approve", content: null)).EnsureSuccessStatusCode();

        var decisions = await DisclosureAsSelfServeUserAsync("admt-reader");
        var entry = Assert.Single(decisions, d => d.Id == id);

        Assert.Equal("approval", entry.Source);
        Assert.Equal("approved", entry.Oversight);
        Assert.Equal("Dana Reviewer", entry.DecidedBy);
        Assert.NotNull(entry.DecidedAt);
        // Enriched from the declaring tool: human label, and the default-High risk tier.
        Assert.Equal("Records a value (side-effecting).", entry.ToolDescription);
        Assert.Equal("high", entry.Risk);
        // The plain-language account: tool label plus the recorded arguments…
        Assert.Contains("value: 42", entry.Summary);
        // …with the agent's stated reasoning lifted out as the decision's basis.
        Assert.Equal("the user asked for it", entry.Basis);
    }

    [Fact]
    public async Task A_rejected_action_discloses_the_human_no()
    {
        var id = SeedDevPendingApproval(toolName: "record", argumentsJson: """{"value":"nope"}""");

        var reviewer = ClientAs("system_admin", "admt-rejecter", name: "Rex Decliner");
        (await reviewer.PostAsync($"/api/chat/approvals/{id}/reject", content: null)).EnsureSuccessStatusCode();

        var entry = Assert.Single(await DisclosureAsSelfServeUserAsync("admt-reader"), d => d.Id == id);

        Assert.Equal("rejected", entry.Oversight);
        Assert.Equal("Rex Decliner", entry.DecidedBy);
    }

    [Fact]
    public async Task An_ungated_execution_discloses_as_automatic()
    {
        var id = SeedDevToolCall("echo", """{"input":"hello"}""", success: true);

        var entry = Assert.Single(await DisclosureAsSelfServeUserAsync("admt-reader"), d => d.Id == id);

        Assert.Equal("audit", entry.Source);
        Assert.Equal("automatic", entry.Oversight);
        // No human decision to attribute, and no review tier to report for an ungated tool.
        Assert.Null(entry.DecidedBy);
        Assert.Null(entry.Risk);
        Assert.Equal("Echoes the given input back to the caller.", entry.ToolDescription);
        Assert.Contains("input: hello", entry.Summary);
    }

    [Fact]
    public async Task A_blocked_call_is_not_double_counted_against_its_approval_record()
    {
        // One agent attempt leaves two traces: the audit row marking the block, and the pending
        // approval carrying the decision. Only the decision may appear in the disclosure.
        var toolName = $"admt.block_{Guid.NewGuid():N}";
        SeedDevToolCall(toolName, argumentsJson: null, success: false,
            error: ToolInvocationMiddleware.ApprovalBlockedError);
        var approvalId = SeedDevPendingApproval(toolName, argumentsJson: null, status: ApprovalStatus.Executed);

        var decisions = await DisclosureAsSelfServeUserAsync("admt-reader", "?take=500");

        var entry = Assert.Single(decisions, d => d.ToolName == toolName);
        Assert.Equal(approvalId, entry.Id);
        Assert.Equal("approved", entry.Oversight);
    }

    [Fact]
    public async Task A_still_pending_approval_is_not_yet_a_decision()
    {
        var toolName = $"admt.pending_{Guid.NewGuid():N}";
        SeedDevPendingApproval(toolName, argumentsJson: null, status: ApprovalStatus.Pending);

        var decisions = await DisclosureAsSelfServeUserAsync("admt-reader", "?take=500");

        Assert.DoesNotContain(decisions, d => d.ToolName == toolName);
    }

    [Fact]
    public async Task The_disclosure_is_scoped_to_the_callers_tenant()
    {
        var (foreignApprovalTool, foreignAuditTool) = SeedForeignTenantHistory();

        var decisions = await DisclosureAsSelfServeUserAsync("admt-reader", "?take=500");

        // Another tenant's AI-decision history must never surface here — from either store.
        Assert.DoesNotContain(decisions, d => d.ToolName == foreignApprovalTool);
        Assert.DoesNotContain(decisions, d => d.ToolName == foreignAuditTool);
    }

    [Fact]
    public async Task A_plain_user_needs_no_admin_permission_to_read_their_disclosure()
    {
        // The acceptance criterion that distinguishes this from /api/admin/audit/*: the plain
        // "user" role (no platform.audit.view, no chat.approvals.manage) reads its own tenant's
        // disclosure. ADMT transparency that only an administrator can see isn't transparency.
        var response = await ClientAs("user", "admt-plain").GetAsync("/api/platform/ai-decisions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Take_and_before_window_the_history_recent_first()
    {
        // Timestamps far in the future make these the globally newest rows, so `take` windows are
        // deterministic even though the class shares one store across tests.
        var prefix = $"admt.win_{Guid.NewGuid():N}";
        var t1 = new DateTimeOffset(2130, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2130, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2130, 1, 3, 0, 0, 0, TimeSpan.Zero);
        SeedDevToolCall($"{prefix}.a", null, success: true, occurredAt: t1);
        SeedDevToolCall($"{prefix}.b", null, success: true, occurredAt: t2);
        SeedDevToolCall($"{prefix}.c", null, success: true, occurredAt: t3);

        var newest = await DisclosureAsSelfServeUserAsync("admt-reader", "?take=2");
        Assert.Equal([$"{prefix}.c", $"{prefix}.b"], newest.Select(d => d.ToolName).ToArray());

        var older = await DisclosureAsSelfServeUserAsync(
            "admt-reader", $"?take=500&before={Uri.EscapeDataString(t2.ToString("O"))}");
        Assert.Contains(older, d => d.ToolName == $"{prefix}.a");
        Assert.DoesNotContain(older, d => d.ToolName == $"{prefix}.b");
        Assert.DoesNotContain(older, d => d.ToolName == $"{prefix}.c");
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private Guid SeedDevPendingApproval(
        string toolName, string? argumentsJson, ApprovalStatus status = ApprovalStatus.Pending)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenantId = db.Tenants.First(t => t.Slug == "dev").Id;

        var approval = new PendingApproval
        {
            TenantId = tenantId,
            ConversationId = Guid.NewGuid(),
            ModuleId = "test",
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            UserDisplay = "Requesting User",
            Status = status,
            ResolvedAt = status == ApprovalStatus.Pending ? null : DateTimeOffset.UtcNow,
        };
        db.Set<PendingApproval>().Add(approval);
        db.SaveChanges();
        return approval.Id;
    }

    private Guid SeedDevToolCall(
        string toolName, string? argumentsJson, bool success, string? error = null,
        DateTimeOffset? occurredAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var tenantId = platform.Tenants.First(t => t.Slug == "dev").Id;

        var entry = new ToolCallAuditEntry
        {
            TenantId = tenantId,
            ModuleId = "test",
            ToolName = toolName,
            Permission = "tools.test.echo",
            ArgumentsJson = argumentsJson,
            UserDisplay = "Requesting User",
            Success = success,
            Error = error,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        };
        audit.ToolCalls.Add(entry);
        audit.SaveChanges();
        return entry.Id;
    }

    private (string ForeignApprovalTool, string ForeignAuditTool) SeedForeignTenantHistory()
    {
        using var scope = _factory.Services.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var tenant = new Tenant { Name = "Foreign", Slug = $"foreign-admt-{Guid.NewGuid():N}" };
        platform.Tenants.Add(tenant);

        var approvalTool = $"foreign.secret_decision_{Guid.NewGuid():N}";
        platform.Set<PendingApproval>().Add(new PendingApproval
        {
            TenantId = tenant.Id,
            ConversationId = Guid.NewGuid(),
            ModuleId = "foreign-module",
            ToolName = approvalTool,
            Status = ApprovalStatus.Executed,
            ResolvedAt = DateTimeOffset.UtcNow,
        });
        platform.SaveChanges();

        var auditTool = $"foreign.secret_call_{Guid.NewGuid():N}";
        audit.ToolCalls.Add(new ToolCallAuditEntry
        {
            TenantId = tenant.Id,
            ModuleId = "foreign-module",
            ToolName = auditTool,
            Permission = "foreign.permission",
            Success = true,
        });
        audit.SaveChanges();

        return (approvalTool, auditTool);
    }

    private sealed record AiDecisionDto(
        Guid Id, string Source, DateTimeOffset OccurredAt, string ModuleId, string? ModuleName,
        string ToolName, string? ToolDescription, string Summary, string? Basis, string Oversight,
        string? Risk, string? RequestedBy, string? DecidedBy, DateTimeOffset? DecidedAt,
        Guid? ConversationId, string? Error);
}

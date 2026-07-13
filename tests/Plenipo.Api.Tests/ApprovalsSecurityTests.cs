using System.Net;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end security coverage of the human-in-the-loop approvals queue. Each pending approval is a
/// side-effecting tool call awaiting authorization, so the queue must be gated on <c>chat.approvals.manage</c>
/// and isolated per tenant — a cross-tenant leak would expose (or let someone resolve) another tenant's pending
/// privileged action. A foreign approval id returns 404 <em>before</em> the executor runs, so isolation is
/// provable without triggering any tool execution.
/// </summary>
public sealed class ApprovalsSecurityTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public ApprovalsSecurityTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient ClientAs(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task A_user_without_manage_approvals_is_forbidden_from_the_queue()
    {
        // The plain user role holds chat.use + chat.conversations.view, but NOT chat.approvals.manage.
        var response = await ClientAs("user", "appr-user").GetAsync("/api/chat/approvals");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Pending_approvals_are_scoped_to_the_callers_tenant()
    {
        var foreign = SeedForeignPendingApproval();

        var response = await ClientAs("system_admin", "appr-op").GetAsync("/api/chat/approvals");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        // Another tenant's pending action must never surface in this tenant's queue — even for system_admin.
        Assert.DoesNotContain(foreign.ToolName, body);
    }

    [Fact]
    public async Task Resolving_another_tenants_pending_approval_is_not_found()
    {
        var foreign = SeedForeignPendingApproval();
        var client = ClientAs("system_admin", "appr-resolve-op");

        // Both verbs resolve the target via a tenant-scoped lookup, so a foreign id is 404 (the approve path
        // never reaches the executor).
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/chat/approvals/{foreign.ApprovalId}/reject", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/chat/approvals/{foreign.ApprovalId}/approve", content: null)).StatusCode);
    }

    private (Guid ApprovalId, string ToolName) SeedForeignPendingApproval()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var tenant = new Tenant { Name = "Foreign", Slug = $"foreign-appr-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);

        var toolName = $"foreign.secret_tool_{Guid.NewGuid():N}";
        var approval = new PendingApproval
        {
            TenantId = tenant.Id,
            ConversationId = Guid.NewGuid(),
            ModuleId = "foreign-module",
            ToolName = toolName,
            Status = ApprovalStatus.Pending,
        };
        db.Set<PendingApproval>().Add(approval);
        db.SaveChanges();

        return (approval.Id, toolName);
    }
}

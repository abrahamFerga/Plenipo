using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Authorization;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// When a side-effecting tool is blocked into the approval queue, the people who can ACT on it
/// (users whose DB-sourced authority grants <c>chat.approvals.manage</c>) each get one in-app
/// notification — category <c>"{moduleId}.approvals"</c>, linking to the approvals surface — while
/// bystanders get nothing and an approver who muted the category is skipped. Approvers learn about
/// blocked actions from their inbox instead of camping in the requester's chat.
/// </summary>
public sealed class ApprovalNotificationTests : IAsyncLifetime
{
    private PlenipoApiFactory _factory = default!;

    public async Task InitializeAsync()
    {
        _factory = new PlenipoApiFactory();
        using var warmup = _factory.CreateClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient ClientAs(string role, string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    [Fact]
    public async Task Blocked_tool_notifies_each_db_enumerable_approver_and_respects_mutes()
    {
        // Everyone must exist BEFORE the block happens — the notifier enumerates approvers at
        // pending-creation time. Token roles deliberately leave no DB rows (RequestEnricher), so
        // approver status is granted below via DB role assignments, exactly what the enumeration
        // is documented to see.
        using var approver = ClientAs("user", "hitl-approver");
        using var muted = ClientAs("user", "hitl-approver-muted");
        using var bystander = ClientAs("user", "hitl-bystander");
        using var requester = ClientAs("system_admin", "hitl-requester");
        foreach (var client in new[] { approver, muted, bystander, requester })
        {
            (await client.GetAsync("/api/platform/me")).EnsureSuccessStatusCode(); // JIT-provision
        }

        await AssignDbRoleAsync("hitl-approver", Roles.TenantAdmin);       // chat.* ⇒ approvals
        await AssignDbRoleAsync("hitl-approver-muted", Roles.TenantAdmin);

        // The muted approver opts out of this module's approval pings via the standard switch.
        var muteResponse = await muted.PutAsJsonAsync(
            "/api/notifications/preferences/test.approvals", new { enabled = false });
        muteResponse.EnsureSuccessStatusCode();

        // The requester's turn attempts the side-effecting tool; the platform blocks it.
        var turn = await requester.PostAsJsonAsync(
            "/api/chat/stream",
            new { moduleId = "test", message = "please use the record tool" });
        turn.EnsureSuccessStatusCode();
        var events = (await turn.Content.ReadFromJsonAsync<List<JsonElement>>())!;
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "ApprovalRequired");

        // The approver's inbox has exactly one actionable ping with the documented shape.
        var ping = Assert.Single(await ApprovalPingsForAsync(approver));
        Assert.Equal("Approval needed: record", ping.GetProperty("title").GetString());
        Assert.Equal("/chat", ping.GetProperty("link").GetString());
        Assert.Contains("'record'", ping.GetProperty("body").GetString());

        // The muted approver and the plain user hear nothing; neither does the requester — the
        // point is reaching people who can act, not echoing the chat back at its own author.
        Assert.Empty(await ApprovalPingsForAsync(muted));
        Assert.Empty(await ApprovalPingsForAsync(bystander));
        Assert.Empty(await ApprovalPingsForAsync(requester));
    }

    /// <summary>The caller's in-app notifications in the "{moduleId}.approvals" category.</summary>
    private static async Task<List<JsonElement>> ApprovalPingsForAsync(HttpClient client)
    {
        var inbox = await client.GetFromJsonAsync<List<JsonElement>>("/api/notifications");
        return inbox!.Where(n => n.GetProperty("category").GetString() == "test.approvals").ToList();
    }

    /// <summary>Grants a DB role assignment — the kind of approver the notifier can enumerate.</summary>
    private async Task AssignDbRoleAsync(string subject, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Subject == subject);
        db.UserRoles.Add(new UserRole { TenantId = user.TenantId, UserId = user.Id, Role = role });
        await db.SaveChangesAsync();
    }
}

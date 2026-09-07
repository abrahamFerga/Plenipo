using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Plenipo.Application.Authorization;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Testing.AgUi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Testing.Conformance;

/// <summary>
/// The security spine, executed against the product's own host: the model never sees a tool the
/// caller may not call, a write is parked for a human rather than performed, a parked write is
/// released only by someone who holds the tool's own permission and executes as the person who
/// asked, every decision and denial reaches the append-only audit, a turn completes the AG-UI
/// protocol and is metered, and the transport never turns a client mistake into a server fault.
/// Each test is numbered after the invariant it proves in docs/TESTING_CONTRACT.md §3.2 and names
/// the fleet issue that proved it was missing.
/// </summary>
public abstract class PlenipoSpineConformance<TProgram>(PlenipoHostFixture<TProgram> fixture)
    where TProgram : class
{
    private const string RequesterName = "Requester Person";
    private const string ApproverName = "Approver Person";

    private ProductContract Contract => fixture.Contract;

    [Fact]
    public async Task S01_A_narrow_role_never_reaches_the_write_tool()
    {
        // Plenipo's signature guarantee: the runner strips tools the caller may not invoke BEFORE
        // building the model request, so the Mock cannot call the write tool even when asked to.
        using var narrow = fixture.ClientFor(Contract.NarrowRole);

        var me = await narrow.GetFromJsonAsync<JsonElement>("/api/platform/me");
        var permissions = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.DoesNotContain("*", permissions);
        Assert.DoesNotContain(Permissions.ForTool(Contract.ModuleId, Contract.WriteTool), permissions);

        var run = await narrow.ChatAsync(Contract.ModuleId, Contract.EffectiveWritePrompt);

        Assert.True(run.Completed, $"The turn must complete for a role that may chat. Stream:\n{run.RawSse}");
        Assert.DoesNotContain(Contract.WriteTool, run.ToolCalls);
    }

    [Fact]
    public async Task S02_A_write_is_parked_for_approval_not_performed()
    {
        using var approver = fixture.ClientFor(Contract.ApproverRole);
        var before = await PendingIdsAsync(approver);

        var run = await approver.ChatAsync(Contract.ModuleId, Contract.EffectiveWritePrompt);

        Assert.True(run.Completed, run.RawSse);
        Assert.Contains(Contract.WriteTool, run.ToolCalls);
        Assert.True(run.RequiredApproval, $"'{Contract.WriteTool}' is declared RequiresApproval, so the turn must emit approval_required. Stream:\n{run.RawSse}");

        var parked = await NewPendingAsync(approver, before);
        Assert.NotNull(parked);
    }

    [Fact]
    public async Task S03_Approving_a_parked_write_executes_it_once_as_the_requester_and_audits_the_decision()
    {
        // #153 / #88: the write was parked by one human and released by another. The tool must run as
        // the one who asked (attribution, separation of duties), the execution must be audited as
        // executed, and the decision must name the one who released it.
        using var requester = fixture.ClientFor(Contract.ApproverRole, subject: "it-requester", displayName: RequesterName);
        using var approver = fixture.ClientFor(Contract.ApproverRole, subject: "it-approver", displayName: ApproverName);
        using var admin = fixture.AdminClient();

        var before = await PendingIdsAsync(approver);
        var run = await requester.ChatAsync(Contract.ModuleId, Contract.EffectiveWritePrompt);
        Assert.True(run.RequiredApproval, run.RawSse);
        var parked = await NewPendingAsync(approver, before);
        Assert.NotNull(parked);
        var id = parked.Value.GetProperty("id").GetString()!;
        var executedBefore = (await ToolCallsAsync(admin)).Count(IsExecutedWrite);

        using var approve = await approver.PostAsync(new Uri($"/api/chat/approvals/{id}/approve", UriKind.Relative), content: null);
        approve.EnsureSuccessStatusCode();
        Assert.DoesNotContain(await PendingIdsAsync(approver), pending => pending == id);

        // Exactly one execution row, attributed to the requester — not to the approver, and not a
        // second "blocked" row (#88).
        await EventuallyAsync(async () => (await ToolCallsAsync(admin)).Count(IsExecutedWrite) == executedBefore + 1,
            "one audited execution of the write tool");
        var executed = (await ToolCallsAsync(admin)).Where(IsExecutedWrite).OrderByDescending(t => t.GetProperty("occurredAt").GetDateTimeOffset()).First();
        Assert.Equal(RequesterName, executed.GetProperty("userDisplay").GetString());

        // The decision names both humans and is in the append-only trail as well as the oversight view.
        await EventuallyAsync(async () => (await AuthEventsAsync(admin)).Any(e =>
                string.Equals(e.GetProperty("eventType").GetString(), "ApprovalDecided", StringComparison.Ordinal)
                && string.Equals(e.GetProperty("subject").GetString(), "it-approver", StringComparison.Ordinal)
                && (e.GetProperty("detail").GetString() ?? "").Contains(id, StringComparison.Ordinal)),
            "an ApprovalDecided auth event by the approver naming the approval");

        var decisions = await admin.GetFromJsonAsync<JsonElement>("/api/platform/ai-decisions");
        Assert.Contains(decisions.EnumerateArray(), d =>
            string.Equals(d.GetProperty("toolName").GetString(), Contract.WriteTool, StringComparison.Ordinal)
            && string.Equals(d.GetProperty("requestedBy").GetString(), RequesterName, StringComparison.Ordinal)
            && string.Equals(d.GetProperty("decidedBy").GetString(), ApproverName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task S04_An_approver_who_lacks_the_tools_own_permission_cannot_release_it()
    {
        // #145: the approvals permission must not be a way to perform an action the role model
        // withholds. A queue-only user holds chat.approvals.manage and nothing else.
        const string queueOnlySubject = "it-queue-only";
        using var admin = fixture.AdminClient();
        using var queueOnly = fixture.ClientFor(Contract.NarrowRole, subject: queueOnlySubject);
        var me = await queueOnly.GetFromJsonAsync<JsonElement>("/api/platform/me");
        var userId = me.GetProperty("userId").GetGuid();
        using (var grant = await admin.PostAsJsonAsync($"/api/admin/users/{userId}/permissions", new { permission = Permissions.ManageApprovals }))
        {
            grant.EnsureSuccessStatusCode();
        }

        using var requester = fixture.ClientFor(Contract.ApproverRole, subject: "it-requester", displayName: RequesterName);
        var before = await PendingIdsAsync(admin);
        await requester.ChatAsync(Contract.ModuleId, Contract.EffectiveWritePrompt);
        var parked = await NewPendingAsync(admin, before);
        Assert.NotNull(parked);
        var id = parked.Value.GetProperty("id").GetString()!;
        var executedBefore = (await ToolCallsAsync(admin)).Count(IsExecutedWrite);
        var deniedBefore = (await AuthEventsAsync(admin)).Count(e => IsDenial(e, queueOnlySubject));

        using (var list = await queueOnly.GetAsync("/api/chat/approvals"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode); // the queue itself is theirs to see
        }

        using var approve = await queueOnly.PostAsync(new Uri($"/api/chat/approvals/{id}/approve", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);

        // Nothing moved: still parked, nothing executed, and the refusal is on the record.
        Assert.Contains(await PendingIdsAsync(admin), pending => pending == id);
        Assert.Equal(executedBefore, (await ToolCallsAsync(admin)).Count(IsExecutedWrite));
        await EventuallyAsync(async () => (await AuthEventsAsync(admin)).Count(e => IsDenial(e, queueOnlySubject)) == deniedBefore + 1,
            "one AccessDenied auth event for the queue-only approver");
        var denial = (await AuthEventsAsync(admin)).First(e => IsDenial(e, queueOnlySubject));
        Assert.Contains(Permissions.ForTool(Contract.ModuleId, Contract.WriteTool), denial.GetProperty("detail").GetString() ?? "", StringComparison.Ordinal);

        using var reject = await admin.PostAsync(new Uri($"/api/chat/approvals/{id}/reject", UriKind.Relative), content: null);
        reject.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task S04a_A_role_without_the_approvals_permission_cannot_decide_a_parked_write()
    {
        // The gate's integrity: a caller who may chat must not be able to approve or reject —
        // otherwise the requester could self-approve their own blocked action.
        using var narrow = fixture.ClientFor(Contract.NarrowRole);

        using var approve = await narrow.PostAsync(new Uri($"/api/chat/approvals/{Guid.NewGuid()}/approve", UriKind.Relative), content: null);
        using var reject = await narrow.PostAsync(new Uri($"/api/chat/approvals/{Guid.NewGuid()}/reject", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
    }

    [Fact]
    public async Task S05_A_conversations_pending_approvals_can_be_listed_alone()
    {
        // #111: "approve this" is informed consent only when the caller can see what asked for it, so
        // the queue must be scopable to one conversation. Unscoped stays the tenant-wide review queue.
        using var approver = fixture.ClientFor(Contract.ApproverRole);
        var before = await PendingIdsAsync(approver);

        await approver.ChatAsync(Contract.ModuleId, Contract.EffectiveWritePrompt, threadId: $"kit-a-{Guid.NewGuid():N}");
        var parkedA = await NewPendingAsync(approver, before);
        Assert.NotNull(parkedA);
        var idA = parkedA.Value.GetProperty("id").GetString()!;
        before.Add(idA);

        await approver.ChatAsync(Contract.ModuleId, Contract.EffectiveWritePrompt, threadId: $"kit-b-{Guid.NewGuid():N}");
        var parkedB = await NewPendingAsync(approver, before);
        Assert.NotNull(parkedB);
        var idB = parkedB.Value.GetProperty("id").GetString()!;

        var conversationA = parkedA.Value.GetProperty("conversationId").GetGuid();
        var conversationB = parkedB.Value.GetProperty("conversationId").GetGuid();
        Assert.NotEqual(conversationA, conversationB);

        var scoped = await approver.GetFromJsonAsync<JsonElement>($"/api/chat/approvals?conversationId={conversationA}");
        var scopedIds = scoped.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToArray();
        Assert.Contains(idA, scopedIds);
        Assert.DoesNotContain(idB, scopedIds);
        Assert.All(scoped.EnumerateArray(), p => Assert.Equal(conversationA, p.GetProperty("conversationId").GetGuid()));

        var all = await PendingIdsAsync(approver);
        Assert.Contains(idA, all);
        Assert.Contains(idB, all);
    }

    [Fact]
    public async Task S06_Rejecting_a_parked_write_leaves_the_queue()
    {
        using var approver = fixture.ClientFor(Contract.ApproverRole);
        var before = await PendingIdsAsync(approver);
        await approver.ChatAsync(Contract.ModuleId, Contract.EffectiveWritePrompt);
        var parked = await NewPendingAsync(approver, before);
        Assert.NotNull(parked);
        var id = parked.Value.GetProperty("id").GetString();

        using var reject = await approver.PostAsync(new Uri($"/api/chat/approvals/{id}/reject", UriKind.Relative), content: null);
        reject.EnsureSuccessStatusCode();

        Assert.DoesNotContain(await PendingIdsAsync(approver), pending => pending == id);
    }

    [Fact]
    public async Task S07_A_permission_denial_is_recorded_in_the_auth_audit()
    {
        // #115: "the boundary held" must be provable after the fact. Exactly one row per denial — a
        // denial recorded twice is as misleading as one not recorded at all.
        const string subject = "it-denied";
        using var admin = fixture.AdminClient();
        using var narrow = fixture.ClientFor(Contract.NarrowRole, subject: subject);
        var before = (await AuthEventsAsync(admin)).Count(e => IsDenial(e, subject));

        using var response = await narrow.GetAsync("/api/chat/approvals");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await EventuallyAsync(async () => (await AuthEventsAsync(admin)).Count(e => IsDenial(e, subject)) == before + 1,
            "exactly one AccessDenied auth event for the denied caller");
        var denial = (await AuthEventsAsync(admin)).First(e => IsDenial(e, subject));
        var detail = denial.GetProperty("detail").GetString() ?? "";
        Assert.Contains(Permissions.ManageApprovals, detail, StringComparison.Ordinal);
        Assert.Contains("/api/chat/approvals", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S09_Dev_auth_never_escalates_a_caller_who_asserts_no_roles()
    {
        // #167: a present-but-empty roles header is an unscoped principal and yields nothing. An
        // absent header yields Auth:Dev:RolesWhenAbsent — the platform's default is system_admin for
        // a smooth first run; a product closes the gap by setting it to "" (a header-stripping proxy
        // must degrade a caller, never escalate one).
        // "," is a present header that parses to zero roles — the shape a shim writes when it must
        // assert "no roles" without the header being dropped in transit.
        using var empty = fixture.RawClient();
        empty.DefaultRequestHeaders.Add("X-Dev-Subject", "it-roleless");
        empty.DefaultRequestHeaders.Add("X-Dev-Tenant", Contract.DevTenant);
        empty.DefaultRequestHeaders.Add("X-Dev-Roles", ",");
        var me = await empty.GetFromJsonAsync<JsonElement>("/api/platform/me");
        Assert.DoesNotContain("*", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        using var absent = fixture.RawClient();
        absent.DefaultRequestHeaders.Add("X-Dev-Subject", "it-headerless");
        absent.DefaultRequestHeaders.Add("X-Dev-Tenant", Contract.DevTenant);
        var whenAbsent = fixture.Factory.Services.GetRequiredService<IConfiguration>()["Auth:Dev:RolesWhenAbsent"];
        var meAbsent = await absent.GetFromJsonAsync<JsonElement>("/api/platform/me");
        var permissions = meAbsent.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        if (whenAbsent is not null && whenAbsent.Trim().Length == 0)
        {
            Assert.DoesNotContain("*", permissions);
        }
        else
        {
            Assert.Contains("*", permissions); // the documented development default, not an escalation
        }
    }

    [Fact]
    public async Task S11_A_plain_turn_completes_the_protocol_in_order_and_is_metered()
    {
        using var approver = fixture.ClientFor(Contract.ApproverRole);

        // A greeting matches no tool, so the Mock streams pure text — the full AG-UI text lifecycle.
        var run = await approver.ChatAsync(Contract.ModuleId, "Hello, how are you today?");

        Assert.True(run.Completed, run.RawSse);
        Assert.Empty(run.ToolCalls);
        Assert.True(run.ReportedUsage, $"The runner must emit CUSTOM(token_usage). Stream:\n{run.RawSse}");

        var started = run.IndexOf("RUN_STARTED");
        var textStart = run.IndexOf("TEXT_MESSAGE_START");
        var textContent = run.IndexOf("TEXT_MESSAGE_CONTENT");
        var textEnd = run.IndexOf("TEXT_MESSAGE_END");
        var finished = run.IndexOf("RUN_FINISHED");
        Assert.True(started >= 0, "missing RUN_STARTED");
        Assert.True(textStart > started, "TEXT_MESSAGE_START must follow RUN_STARTED");
        Assert.True(textContent > textStart, "TEXT_MESSAGE_CONTENT must follow TEXT_MESSAGE_START");
        Assert.True(textEnd > textContent, "TEXT_MESSAGE_END must follow TEXT_MESSAGE_CONTENT");
        Assert.True(finished > textEnd, "RUN_FINISHED must follow TEXT_MESSAGE_END");

        using var admin = fixture.AdminClient();
        var usage = await admin.GetFromJsonAsync<JsonElement>("/api/admin/usage?days=30");
        Assert.True(usage.GetProperty("totalTokens").GetInt32() > 0, "token usage must be recorded, not a stubbed zero");
        Assert.True(usage.GetProperty("turns").GetInt32() > 0);
        Assert.Contains(usage.GetProperty("byModule").EnumerateArray(),
            m => string.Equals(m.GetProperty("moduleId").GetString(), Contract.ModuleId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task S12_An_unreadable_body_is_a_client_error_not_a_server_fault()
    {
        // #176: a request the framework cannot read is the client's mistake, and ASP.NET says so with a
        // BadHttpRequestException carrying 400. The platform's exception handler must keep that status.
        using var approver = fixture.ClientFor(Contract.ApproverRole);
        var route = new Uri($"/api/agui/{Contract.ModuleId}", UriKind.Relative);

        using (var noBody = await approver.PostAsync(route, content: null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        }

        using (var notJson = await approver.PostAsync(route, new StringContent("{ not json", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        }

        // A readable but meaningless body is answered in protocol, not with a status.
        using var empty = await approver.PostAsync(route, new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Contains("RUN_ERROR", await empty.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S13_Health_endpoints_answer_in_Production()
    {
        // Liveness and readiness probes hit a Production host; a health endpoint mapped only in
        // Development leaves the container unhealthy forever.
        await using var production = fixture.DeriveProduction();
        using var client = production.CreateClient();

        using var alive = await client.GetAsync("/alive");
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task S14_Concurrent_first_touch_requests_provision_exactly_one_user()
    {
        // networthy#215: a shell fans out several requests on the first click for a new persona; the
        // losers of the provisioning race must converge on the winner's row, never surface a 500.
        var subject = $"it-race-{Guid.NewGuid():N}";
        var clients = Enumerable.Range(0, 6).Select(_ => fixture.ClientFor(Contract.NarrowRole, subject: subject)).ToArray();
        try
        {
            var responses = await Task.WhenAll(clients.Select(c => c.GetAsync("/api/platform/me")));
            try
            {
                Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.Equal(1, await db.Users.IgnoreQueryFilters().CountAsync(u => u.Subject == subject));
    }

    private bool IsExecutedWrite(JsonElement toolCall) =>
        string.Equals(toolCall.GetProperty("toolName").GetString(), Contract.WriteTool, StringComparison.Ordinal)
        && toolCall.GetProperty("success").GetBoolean();

    private static bool IsDenial(JsonElement authEvent, string subject) =>
        string.Equals(authEvent.GetProperty("eventType").GetString(), "AccessDenied", StringComparison.Ordinal)
        && string.Equals(authEvent.GetProperty("subject").GetString(), subject, StringComparison.Ordinal);

    private static async Task<List<JsonElement>> ToolCallsAsync(HttpClient admin)
    {
        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls?take=500");
        return audit.EnumerateArray().ToList();
    }

    private static async Task<List<JsonElement>> AuthEventsAsync(HttpClient admin)
    {
        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit/auth-events?take=500");
        return audit.EnumerateArray().ToList();
    }

    /// <summary>Audit writes ride the outbox, so a just-caused row may land a moment after the response.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition, string expectation)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"Timed out waiting for {expectation}.");
    }

    [Fact]
    public async Task S15_A_served_shell_carries_per_request_nonces_that_the_policy_admits()
    {
        // #197: a product that ships a strict CSP must never have to pin a hash of platform-authored
        // HTML. Wherever this host serves a shell, every script carries a fresh nonce, the response
        // is uncacheable, and a policy the host emits admits that nonce. A host serving no shell
        // (UI from a dev server, API-only) has nothing to prove here.
        using var client = fixture.RawClient();
        foreach (var route in new[] { "/", "/admin/" })
        {
            using var response = await client.GetAsync(new Uri(route, UriKind.Relative));
            if (response.StatusCode != HttpStatusCode.OK
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var html = await response.Content.ReadAsStringAsync();
            var scripts = System.Text.RegularExpressions.Regex.Matches(html, "<script\\b([^>]*)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (scripts.Count == 0)
            {
                continue;
            }

            var nonces = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match script in scripts)
            {
                var nonce = System.Text.RegularExpressions.Regex.Match(script.Groups[1].Value, "\\bnonce=\"([^\"]+)\"").Groups[1].Value;
                Assert.False(string.IsNullOrEmpty(nonce), $"GET {route}: a <script> tag carries no nonce: <script{script.Groups[1].Value}>");
                nonces.Add(nonce);
            }

            var stamped = Assert.Single(nonces);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

            if (response.Headers.TryGetValues("Content-Security-Policy", out var policies))
            {
                var policy = string.Join("; ", policies);
                if (policy.Contains("script-src", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Contains($"'nonce-{stamped}'", policy, StringComparison.Ordinal);
                }
            }

            using var again = await client.GetAsync(new Uri(route, UriKind.Relative));
            Assert.DoesNotContain(stamped, await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    private static async Task<HashSet<string>> PendingIdsAsync(HttpClient client)
    {
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        return pending.EnumerateArray()
            .Select(p => p.GetProperty("id").GetString() ?? "")
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The approval for the contract's write tool that appeared since <paramref name="before"/>.</summary>
    private async Task<JsonElement?> NewPendingAsync(HttpClient client, HashSet<string> before)
    {
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        foreach (var approval in pending.EnumerateArray())
        {
            var id = approval.GetProperty("id").GetString() ?? "";
            var tool = approval.GetProperty("toolName").GetString();
            if (!before.Contains(id) && string.Equals(tool, Contract.WriteTool, StringComparison.Ordinal))
            {
                return approval;
            }
        }

        return null;
    }
}

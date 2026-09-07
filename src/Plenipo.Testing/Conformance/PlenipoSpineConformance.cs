using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Authorization;
using Plenipo.Testing.AgUi;
using Xunit;

namespace Plenipo.Testing.Conformance;

/// <summary>
/// The security spine, executed against the product's own host: the model never sees a tool the
/// caller may not call, a write is parked for a human rather than performed, only a holder of the
/// approvals permission can decide, and a turn completes the AG-UI protocol and is metered. Each
/// test is numbered after the invariant it proves in docs/TESTING_CONTRACT.md §3.2. Invariants the
/// platform has accepted but not yet shipped (S3's requester identity and three audit rows, S4's
/// per-tool permission on approve, S5, S7–S10, S12–S15) are added here as they land, so a product
/// receives them by upgrading.
/// </summary>
public abstract class PlenipoSpineConformance<TProgram>(PlenipoHostFixture<TProgram> fixture)
    where TProgram : class
{
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
    public async Task S03_Approving_a_parked_write_executes_it_and_leaves_the_queue()
    {
        using var approver = fixture.ClientFor(Contract.ApproverRole);
        var before = await PendingIdsAsync(approver);
        await approver.ChatAsync(Contract.ModuleId, Contract.EffectiveWritePrompt);
        var parked = await NewPendingAsync(approver, before);
        Assert.NotNull(parked);
        var id = parked.Value.GetProperty("id").GetString();

        using var approve = await approver.PostAsync(new Uri($"/api/chat/approvals/{id}/approve", UriKind.Relative), content: null);
        approve.EnsureSuccessStatusCode();

        Assert.DoesNotContain(await PendingIdsAsync(approver), pending => pending == id);

        // The call is on the audit trail. (Its recorded outcome and the identity it executed as are
        // platform requests #88 and #153; those assertions arrive with the release that fixes them.)
        using var admin = fixture.AdminClient();
        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls?take=100");
        Assert.Contains(audit.EnumerateArray(), entry =>
            string.Equals(entry.GetProperty("toolName").GetString(), Contract.WriteTool, StringComparison.Ordinal));
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

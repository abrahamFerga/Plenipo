using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// End-to-end API behaviour through the real host: module discovery, the security catalog, and the RBAC /
/// approval permission gates. These automate the checks previously verified by hand and guard against the
/// exact regressions hit during development (a mis-wired permission gate, broken module loading).
/// </summary>
[Collection("api")]
public sealed class ApiIntegrationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Modules_ExposesThreeVerticals()
    {
        var modules = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");

        var ids = modules.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToArray();
        Assert.Contains("finance", ids);
        Assert.Contains("nutrition", ids);
        Assert.Contains("legal", ids);
    }

    [Fact]
    public async Task Modules_DataTabs_DeclareEndpointAndColumns()
    {
        var modules = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");

        var finance = modules.EnumerateArray().First(m => m.GetProperty("id").GetString() == "finance");
        var transactions = finance.GetProperty("tabs").EnumerateArray()
            .First(t => t.GetProperty("id").GetString() == "transactions");

        Assert.Equal("/api/finance/transactions", transactions.GetProperty("dataEndpoint").GetString());
        Assert.NotEmpty(transactions.GetProperty("columns").EnumerateArray());
    }

    [Fact]
    public async Task TabEditor_ShipsOnlyToCallersHoldingItsPermission()
    {
        // A wildcard admin gets the clauses tab's editor affordances…
        var admin = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var adminClauses = admin.EnumerateArray().First(m => m.GetProperty("id").GetString() == "legal")
            .GetProperty("tabs").EnumerateArray().First(t => t.GetProperty("id").GetString() == "clauses");
        var editor = adminClauses.GetProperty("editor");
        Assert.Equal("/api/legal/clauses", editor.GetProperty("upsertEndpoint").GetString());
        Assert.Equal("slug", editor.GetProperty("keyField").GetString());
        Assert.NotEmpty(editor.GetProperty("fields").EnumerateArray());

        // …a plain user who can VIEW clauses but not manage the library gets a read-only tab: the
        // payload itself never advertises affordances the caller can't use.
        using var user = fixture.ClientFor("user");
        // 'user' lacks legal.clauses.view by default; grant view-only via an explicit role? The dev
        // baseline: legal tabs need module role perms — use tenant_admin, who can view but does NOT
        // hold legal.library.manage.
        var viewer = await fixture.ClientFor("tenant_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var viewerLegal = viewer.EnumerateArray().FirstOrDefault(m => m.GetProperty("id").GetString() == "legal");
        if (viewerLegal.ValueKind == JsonValueKind.Object)
        {
            var viewerClauses = viewerLegal.GetProperty("tabs").EnumerateArray()
                .FirstOrDefault(t => t.GetProperty("id").GetString() == "clauses");
            if (viewerClauses.ValueKind == JsonValueKind.Object)
            {
                Assert.True(
                    !viewerClauses.TryGetProperty("editor", out var e) || e.ValueKind == JsonValueKind.Null,
                    "a caller without legal.library.manage must not receive editor affordances");
            }
        }
    }

    [Fact]
    public async Task TabEditor_BudgetsDeclaresNumericLimitField()
    {
        var modules = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var budgets = modules.EnumerateArray().First(m => m.GetProperty("id").GetString() == "finance")
            .GetProperty("tabs").EnumerateArray().First(t => t.GetProperty("id").GetString() == "budgets");

        var editor = budgets.GetProperty("editor");
        Assert.Equal("/api/finance/budgets", editor.GetProperty("upsertEndpoint").GetString());
        Assert.Equal("category", editor.GetProperty("keyField").GetString());

        // monthlyLimit binds to a decimal server-side — the shell must know to post a JSON number.
        var limit = editor.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("field").GetString() == "monthlyLimit");
        Assert.True(limit.GetProperty("numeric").GetBoolean());
    }

    [Fact]
    public async Task TabEditor_DiaryIsAddOnly()
    {
        var modules = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var diary = modules.EnumerateArray().First(m => m.GetProperty("id").GetString() == "nutrition")
            .GetProperty("tabs").EnumerateArray().First(t => t.GetProperty("id").GetString() == "diary");

        // No keyField and no deleteEndpoint: the shell offers Add but no per-row Edit/Delete —
        // diary entries are records of what was eaten, not editable documents.
        var editor = diary.GetProperty("editor");
        Assert.Equal("/api/nutrition/diary", editor.GetProperty("upsertEndpoint").GetString());
        Assert.True(editor.GetProperty("keyField").ValueKind == JsonValueKind.Null);
        Assert.True(!editor.TryGetProperty("deleteEndpoint", out var del) || del.ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData("finance", "/api/finance/transactions")]
    [InlineData("nutrition", "/api/nutrition/foods")]
    [InlineData("legal", "/api/legal/clauses")]
    public async Task DataTabs_ColumnsMatchTheEndpointRows(string moduleId, string endpoint)
    {
        var client = fixture.ClientFor("system_admin");

        // The columns the shell renders for this module's server-driven tab…
        var modules = await client.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var module = modules.EnumerateArray().First(m => m.GetProperty("id").GetString() == moduleId);
        var tab = module.GetProperty("tabs").EnumerateArray()
            .First(t => t.TryGetProperty("dataEndpoint", out var de)
                && de.ValueKind == JsonValueKind.String && de.GetString() == endpoint);
        var columns = tab.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("field").GetString()!).ToArray();
        Assert.NotEmpty(columns);

        // …must each be a real (camelCased) property on the rows the endpoint returns, or the tab silently
        // renders a blank column. Guards every demo vertical against the mismatch the Tasks template tests for.
        var rows = await client.GetFromJsonAsync<JsonElement>(endpoint);
        Assert.True(rows.GetArrayLength() > 0, $"{moduleId} endpoint returned no rows to verify columns against");
        var firstRow = rows.EnumerateArray().First();
        foreach (var column in columns)
        {
            Assert.True(firstRow.TryGetProperty(column, out _), $"{moduleId} rows have no '{column}' property");
        }
    }

    [Fact]
    public async Task Modules_ExposeSuggestedPrompts()
    {
        var modules = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");

        // The chat surfaces these as one-click starters so a newcomer can immediately exercise the tools.
        var finance = modules.EnumerateArray().First(m => m.GetProperty("id").GetString() == "finance");
        var prompts = finance.GetProperty("suggestedPrompts").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.NotEmpty(prompts);
        Assert.Contains("Summarize my spending", prompts);
    }

    [Fact]
    public async Task Legal_matters_tab_is_live_with_a_data_endpoint()
    {
        var modules = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/modules");

        // The Matters tab graduated from placeholder to a live server-driven table.
        var legal = modules.EnumerateArray().First(m => m.GetProperty("id").GetString() == "legal");
        var matters = legal.GetProperty("tabs").EnumerateArray().First(t => t.GetProperty("id").GetString() == "matters");
        Assert.Equal("/api/legal/matters", matters.GetProperty("dataEndpoint").GetString());
        Assert.True(matters.GetProperty("columns").GetArrayLength() >= 4);
    }

    [Fact]
    public async Task SecurityCatalog_FlagsRecordTransactionForApproval()
    {
        var catalog = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/admin/security/catalog");

        var finance = catalog.GetProperty("modules").EnumerateArray()
            .First(m => m.GetProperty("id").GetString() == "finance");
        var record = finance.GetProperty("tools").EnumerateArray()
            .First(t => t.GetProperty("permission").GetString()!.EndsWith("record_transaction", StringComparison.Ordinal));

        Assert.True(record.GetProperty("requiresApproval").GetBoolean());
    }

    [Theory]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData("system_admin", HttpStatusCode.OK)]
    public async Task AdminUsers_GatedByManageUsers(string role, HttpStatusCode expected)
    {
        using var response = await fixture.ClientFor(role).GetAsync("/api/admin/users");
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("user", HttpStatusCode.Forbidden)]
    [InlineData("guest", HttpStatusCode.Forbidden)]
    [InlineData("tenant_admin", HttpStatusCode.OK)]
    [InlineData("system_admin", HttpStatusCode.OK)]
    public async Task Approvals_GatedByManageApprovals(string role, HttpStatusCode expected)
    {
        using var response = await fixture.ClientFor(role).GetAsync("/api/chat/approvals");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Info_ReportsDemoModeUnderMockProvider()
    {
        var info = await fixture.ClientFor("system_admin").GetFromJsonAsync<JsonElement>("/api/platform/info");

        Assert.True(info.GetProperty("chatEnabled").GetBoolean());
        Assert.True(info.GetProperty("demoMode").GetBoolean()); // the test host runs the Mock provider
        // The upload limit the composer preflights against — same value FileEndpoints enforces.
        Assert.Equal(20 * 1024 * 1024, info.GetProperty("maxUploadBytes").GetInt64());
    }

    [Fact]
    public async Task Me_ReportsTenantAndChatPermission()
    {
        var me = await fixture.ClientFor("user").GetFromJsonAsync<JsonElement>("/api/platform/me");

        Assert.False(string.IsNullOrWhiteSpace(me.GetProperty("tenantId").GetString()));
        var perms = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Contains("chat.use", perms);
        Assert.DoesNotContain("chat.approvals.manage", perms);
    }

    [Fact]
    public async Task Agui_PlainChat_EmitsTheTextMessageLifecycle_InOrder()
    {
        var client = fixture.ClientFor("system_admin");

        // A greeting matches no tool, so the Mock streams pure text — exercising the AG-UI text-message
        // frames (TEXT_MESSAGE_START/CONTENT/END) that the tool-focused tests don't assert.
        var body = new { messages = new[] { new { role = "user", content = "Hello, how are you today?" } } };

        using var response = await client.PostAsJsonAsync("/api/agui/finance", body);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var sse = await response.Content.ReadAsStringAsync();

        // A plain greeting calls no tool and must not error.
        Assert.DoesNotContain("TOOL_CALL_START", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN_ERROR", sse, StringComparison.Ordinal);

        // The full AG-UI text lifecycle must appear, in protocol order.
        var runStarted = sse.IndexOf("RUN_STARTED", StringComparison.Ordinal);
        var textStart = sse.IndexOf("TEXT_MESSAGE_START", StringComparison.Ordinal);
        var textContent = sse.IndexOf("TEXT_MESSAGE_CONTENT", StringComparison.Ordinal);
        var textEnd = sse.IndexOf("TEXT_MESSAGE_END", StringComparison.Ordinal);
        var runFinished = sse.IndexOf("RUN_FINISHED", StringComparison.Ordinal);

        Assert.True(runStarted >= 0, "missing RUN_STARTED");
        Assert.True(textStart > runStarted, "TEXT_MESSAGE_START must follow RUN_STARTED");
        Assert.True(textContent > textStart, "TEXT_MESSAGE_CONTENT must follow TEXT_MESSAGE_START");
        Assert.True(textEnd > textContent, "TEXT_MESSAGE_END must follow TEXT_MESSAGE_CONTENT");
        Assert.True(runFinished > textEnd, "RUN_FINISHED must follow TEXT_MESSAGE_END");
    }

    [Fact]
    public async Task Agui_RestrictedUser_HasModuleToolsFilteredBeforeTheModelCall()
    {
        // Plenipo's signature guarantee: the model never sees a tool the caller may not invoke. A plain "user"
        // has chat.use but NOT tools.finance.*, so the agent runner strips the finance tools before building
        // the model request — the Mock cannot call a tool it never received, even when asked to.
        var client = fixture.ClientFor("user");

        var body = new
        {
            messages = new[] { new { role = "user", content = "Please summarize my spending using a tool." } },
        };

        using var response = await client.PostAsJsonAsync("/api/agui/finance", body);
        response.EnsureSuccessStatusCode();
        var sse = await response.Content.ReadAsStringAsync();

        // The run still completes (chat.use is enough to chat) — the tool is filtered, not the request refused…
        Assert.Contains("RUN_FINISHED", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN_ERROR", sse, StringComparison.Ordinal);
        // …but the finance tool is never invoked, because this user may not call summarize_spending.
        // (The user baseline legitimately includes the platform document tools, so a TOOL_CALL for
        // one of those may occur; the forbidden module tool must not.)
        Assert.DoesNotContain("summarize_spending", sse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenants_AreIsolated_OneTenantNeverSeesAnothersAuditLog()
    {
        await fixture.EnsureTenantAsync("other");

        // In the dev tenant, drive an audited tool call so the dev audit log is non-empty.
        var dev = fixture.ClientFor("system_admin");
        using (var run = await dev.PostAsJsonAsync("/api/agui/finance",
            new { messages = new[] { new { role = "user", content = "Summarize my spending using a tool." } } }))
        {
            run.EnsureSuccessStatusCode();
            await run.Content.ReadAsStringAsync(); // drain the SSE so the tool call + audit write complete
        }
        var devAudit = await dev.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls?take=50");
        Assert.NotEmpty(devAudit.EnumerateArray());

        // A system_admin in a DIFFERENT tenant must see NONE of the dev tenant's tool-call audit — the audit
        // store is tenant-isolated like the platform data, even though it lives in a separate database.
        var other = fixture.ClientForTenant("system_admin", "other");
        var otherAudit = await other.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls?take=50");
        Assert.Empty(otherAudit.EnumerateArray());
    }

    [Fact]
    public async Task Agui_SameThreadId_ContinuesTheSameConversation()
    {
        var client = fixture.ClientFor("system_admin");
        var threadId = "agui-" + Guid.NewGuid().ToString("N")[..8]; // a non-GUID thread id, as real AG-UI clients use

        var first = ConversationIdOf(await RunAguiTurnAsync(client, threadId, "Hello there"));
        var second = ConversationIdOf(await RunAguiTurnAsync(client, threadId, "What did I just say?"));

        Assert.False(string.IsNullOrEmpty(first));
        // The client owns the thread id; reusing it must resolve to the SAME conversation — not a fresh one
        // each turn (the bug: a non-GUID thread id used to map to null → a new conversation every turn).
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Agui_ThreadIdThatIsAConversationId_ResumesThatConversation()
    {
        var client = fixture.ClientFor("system_admin");

        // Start a conversation under a client-owned thread id, then RESUME it by using the SERVER's
        // conversation id as the thread id — how the web UI continues a conversation picked from
        // history over AG-UI (the id round-trips through RUN_FINISHED.result.conversationId).
        var threadId = "resume-" + Guid.NewGuid().ToString("N")[..8];
        var conversationId = ConversationIdOf(await RunAguiTurnAsync(client, threadId, "Hello there"));
        Assert.False(string.IsNullOrEmpty(conversationId));

        var resumed = ConversationIdOf(await RunAguiTurnAsync(client, conversationId!, "And hello again"));
        Assert.Equal(conversationId, resumed);

        var conversation = await fixture.GetConversationAsync(Guid.Parse(conversationId!));
        Assert.Equal(4, conversation.Messages.Count); // both turns landed in the SAME conversation
    }

    [Fact]
    public async Task TokenUsage_IsRecorded_AndReportedByTheAdminUsageEndpoint()
    {
        var client = fixture.ClientFor("system_admin");

        // A chat turn streams token usage through the pipeline; draining the SSE ensures the row is written.
        using (var run = await client.PostAsJsonAsync("/api/agui/finance",
            new { messages = new[] { new { role = "user", content = "Hello, what can you do?" } } }))
        {
            run.EnsureSuccessStatusCode();
            await run.Content.ReadAsStringAsync();
        }

        var usage = await client.GetFromJsonAsync<JsonElement>("/api/admin/usage?days=30");

        // The per-turn token tracking the admin dashboard surfaces is actually populated — not a stubbed zero.
        Assert.True(usage.GetProperty("totalTokens").GetInt32() > 0);
        Assert.True(usage.GetProperty("turns").GetInt32() > 0);
        Assert.Contains(usage.GetProperty("byModule").EnumerateArray(),
            m => m.GetProperty("moduleId").GetString() == "finance");
    }

    [Fact]
    public async Task Conversation_PersistsAndAccumulatesItsMessageHistory_AcrossTurns()
    {
        var client = fixture.ClientFor("system_admin");
        var threadId = "persist-" + Guid.NewGuid().ToString("N")[..8];

        await RunAguiTurnAsync(client, threadId, "Hello there");
        var conversationId = ConversationIdOf(await RunAguiTurnAsync(client, threadId, "And hello again"));
        Assert.False(string.IsNullOrEmpty(conversationId));

        // Each turn persists the user + assistant messages to the SAME conversation, so the agent replays the
        // full history next turn. Plenipo resumes from these stored messages (the runner uses no MAF session).
        var conversation = await fixture.GetConversationAsync(Guid.Parse(conversationId!));
        Assert.Equal(4, conversation.Messages.Count); // two turns × (user + assistant)
    }

    [Fact]
    public async Task Chat_MockProvider_DrivesARealAuditedToolCall()
    {
        // system_admin has chat.use AND every tool permission, so no tool is filtered out pre-model-call.
        var client = fixture.ClientFor("system_admin");

        var body = new
        {
            messages = new[] { new { role = "user", content = "Please summarize my spending using a tool." } },
        };

        using var response = await client.PostAsJsonAsync("/api/agui/finance", body);
        response.EnsureSuccessStatusCode();
        var sse = await response.Content.ReadAsStringAsync();

        // The zero-key Mock provider actually invokes a tool through the real agent pipeline (not just
        // listing names): the AG-UI stream surfaces the call and the run completes without error.
        Assert.Contains("TOOL_CALL_START", sse, StringComparison.Ordinal);
        Assert.Contains("RUN_FINISHED", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN_ERROR", sse, StringComparison.Ordinal);

        // The tool actually ran over the seeded ledger and returned real category totals — not an empty
        // result. Regression guard: the Mock used to synthesize the OPTIONAL `days` arg as 1, so
        // summarize_spending looked back a single day and reported "No spending" despite the demo ledger.
        Assert.DoesNotContain("No spending", sse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Groceries", sse, StringComparison.Ordinal);

        // …and the invocation was audited with a COMPLETE record — exactly what the Admin → Audit view shows:
        // the tool, the permission that gated it, the outcome, and how long it took.
        var audit = await client.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls?take=50");
        var summarize = audit.EnumerateArray().First(c =>
            c.GetProperty("moduleId").GetString() == "finance" &&
            c.GetProperty("toolName").GetString() == "summarize_spending");
        Assert.Equal("tools.finance.summarize_spending", summarize.GetProperty("permission").GetString());
        Assert.True(summarize.GetProperty("success").GetBoolean());
        Assert.True(summarize.GetProperty("durationMs").GetInt64() >= 0);
    }

    [Fact]
    public async Task Finance_ShipsWithSeededDemoLedger()
    {
        var client = fixture.ClientFor("system_admin");

        // The demo seeds a realistic ledger in Development so the tabs and the spending/budget tools
        // aren't hollow on first run.
        var transactions = await client.GetFromJsonAsync<JsonElement>("/api/finance/transactions");
        Assert.NotEmpty(transactions.EnumerateArray());

        var budgets = await client.GetFromJsonAsync<JsonElement>("/api/finance/budgets");
        Assert.NotEmpty(budgets.EnumerateArray());
    }

    [Fact]
    public async Task Chat_MockProvider_TriggersApprovalGate_ForSideEffectingTool()
    {
        var client = fixture.ClientFor("system_admin");

        var body = new
        {
            messages = new[] { new { role = "user", content = "Record a transaction for me." } },
        };

        using var response = await client.PostAsJsonAsync("/api/agui/finance", body);
        response.EnsureSuccessStatusCode();
        var sse = await response.Content.ReadAsStringAsync();

        // record_transaction is side-effecting (RequiresApproval) — the Mock calls it, but the pipeline
        // BLOCKS auto-execution and routes it to human approval. No API key involved.
        Assert.Contains("approval_required", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN_ERROR", sse, StringComparison.Ordinal);

        // The blocked call is now a pending approval the user can act on from the Approvals panel.
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        var recordApprovals = pending.EnumerateArray()
            .Where(p => p.GetProperty("toolName").GetString() == "record_transaction")
            .ToArray();
        Assert.NotEmpty(recordApprovals);
    }

    [Fact]
    public async Task Chat_NutritionSearch_ReturnsRealCatalogFood()
    {
        var client = fixture.ClientFor("system_admin");
        var body = new { messages = new[] { new { role = "user", content = "Search for chicken" } } };

        using var response = await client.PostAsJsonAsync("/api/agui/nutrition", body);
        response.EnsureSuccessStatusCode();
        var sse = await response.Content.ReadAsStringAsync();

        // The Mock passes the user's term to search_foods, which finds a real catalog food. Regression guard:
        // tools with a required string used to get "example" and return "no match". (Single word — the Mock
        // streams word-by-word, so "Chicken breast" spans separate SSE deltas.)
        Assert.Contains("TOOL_CALL_START", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN_ERROR", sse, StringComparison.Ordinal);
        Assert.Contains("Chicken", sse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chat_LegalDraft_ReturnsRealClause()
    {
        var client = fixture.ClientFor("system_admin");
        var body = new { messages = new[] { new { role = "user", content = "Draft a confidentiality clause" } } };

        using var response = await client.PostAsJsonAsync("/api/agui/legal", body);
        response.EnsureSuccessStatusCode();
        var sse = await response.Content.ReadAsStringAsync();

        Assert.Contains("TOOL_CALL_START", sse, StringComparison.Ordinal);
        Assert.DoesNotContain("RUN_ERROR", sse, StringComparison.Ordinal);
        Assert.Contains("Confidentiality", sse, StringComparison.Ordinal); // a real clause was drafted
    }

    [Fact]
    public async Task Approval_MutationsRequireManageApprovals_NotJustChatAccess()
    {
        // The human-in-the-loop gate's integrity: a plain "user" can chat (chat.use) but must NOT be able to
        // approve or reject a blocked side-effecting tool — that needs chat.approvals.manage. Otherwise the
        // requester could self-approve their own blocked action, defeating the gate.
        var client = fixture.ClientFor("user");

        using var approve = await client.PostAsync($"/api/chat/approvals/{Guid.NewGuid()}/approve", content: null);
        using var reject = await client.PostAsync($"/api/chat/approvals/{Guid.NewGuid()}/reject", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, approve.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reject.StatusCode);
    }

    [Fact]
    public async Task Approval_Approving_ReExecutesTheBlockedToolAndWritesData()
    {
        // The signature security flow, end-to-end: a side-effecting tool is blocked (deny-by-default),
        // recorded as a pending approval, then APPROVED — which re-executes the exact blocked call and
        // actually writes data. A unique marker keeps this order-independent in the shared fixture.
        var client = fixture.ClientFor("system_admin");
        var marker = "rt" + Guid.NewGuid().ToString("N")[..8];

        var body = new { messages = new[] { new { role = "user", content = $"Record a transaction {marker}" } } };
        using var chat = await client.PostAsJsonAsync("/api/agui/finance", body);
        chat.EnsureSuccessStatusCode();
        Assert.Contains("approval_required", await chat.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Find THIS approval — its recorded arguments carry the marker as the description.
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        var approval = pending.EnumerateArray().First(p =>
            p.GetProperty("toolName").GetString() == "record_transaction" &&
            (p.TryGetProperty("argumentsJson", out var a) ? a.GetString() ?? "" : "").Contains(marker, StringComparison.Ordinal));
        var id = approval.GetProperty("id").GetString();

        // Approve → the platform re-executes the exact blocked call (with the coerced JSON arguments).
        using var approve = await client.PostAsync($"/api/chat/approvals/{id}/approve", content: null);
        approve.EnsureSuccessStatusCode();

        // The transaction was actually written — block → approve → execute works.
        var txns = await client.GetFromJsonAsync<JsonElement>("/api/finance/transactions");
        Assert.Contains(txns.EnumerateArray(), t =>
            (t.GetProperty("description").GetString() ?? "").Contains(marker, StringComparison.Ordinal));

        // …and it's no longer pending.
        var after = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        Assert.DoesNotContain(after.EnumerateArray(), p => p.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task Approval_Rejecting_DiscardsTheBlockedToolWithoutWriting()
    {
        // The other half of the gate: rejecting a blocked side-effecting tool discards it — nothing is written.
        var client = fixture.ClientFor("system_admin");
        var marker = "rj" + Guid.NewGuid().ToString("N")[..8];

        var body = new { messages = new[] { new { role = "user", content = $"Record a transaction {marker}" } } };
        using var chat = await client.PostAsJsonAsync("/api/agui/finance", body);
        chat.EnsureSuccessStatusCode();

        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        var approval = pending.EnumerateArray().First(p =>
            p.GetProperty("toolName").GetString() == "record_transaction" &&
            (p.TryGetProperty("argumentsJson", out var a) ? a.GetString() ?? "" : "").Contains(marker, StringComparison.Ordinal));
        var id = approval.GetProperty("id").GetString();

        using var reject = await client.PostAsync($"/api/chat/approvals/{id}/reject", content: null);
        reject.EnsureSuccessStatusCode();

        // No transaction was written (the side effect was denied)…
        var txns = await client.GetFromJsonAsync<JsonElement>("/api/finance/transactions");
        Assert.DoesNotContain(txns.EnumerateArray(), t =>
            (t.GetProperty("description").GetString() ?? "").Contains(marker, StringComparison.Ordinal));

        // …and it's no longer pending.
        var after = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        Assert.DoesNotContain(after.EnumerateArray(), p => p.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task HealthEndpoints_AreMappedInProduction()
    {
        // Regression guard for the deploy bug: health endpoints used to be mapped only in Development, so a
        // production deployment's liveness/readiness probes (and the post-deploy smoke) hit an unmapped 404
        // and the container never went healthy. They must respond in Production too.
        await using var factory = new ProductionApiFactory();
        using var client = factory.CreateClient();

        using var alive = await client.GetAsync("/alive");
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Host_StartsCleanly_AgainstAnAlreadySeededDatabase()
    {
        // The shared fixture already booted + seeded the dev tenant and enabled its modules in this database.
        // Booting a SECOND Development host against the same data must be idempotent — a newcomer who stops
        // and re-runs `dotnet run` (the docker-compose Postgres persists) hits exactly this. Regression guard
        // for the SeedDevTenant duplicate-key crash (the TenantModules existence check was hidden by the tenant
        // query filter at startup, so it re-inserted and violated IX_tenant_modules_TenantId_ModuleId).
        await using var factory = new DevelopmentRestartFactory();
        using var client = factory.CreateClient();

        using var alive = await client.GetAsync("/alive");
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);

        // Modules are still served (seeding completed, didn't crash).
        var modules = await client.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        Assert.NotEmpty(modules.EnumerateArray());
    }

    [Fact]
    public async Task Conversations_AreListedForTheUser_AndMessagesReadBack()
    {
        var client = fixture.ClientFor("system_admin");
        var threadId = "hist-" + Guid.NewGuid().ToString("N")[..8];
        var marker = "probe-" + Guid.NewGuid().ToString("N")[..6];

        var conversationId = ConversationIdOf(await RunAguiTurnAsync(client, threadId, $"Hello there {marker}"));
        Assert.False(string.IsNullOrEmpty(conversationId));

        // The conversation shows up in the user's history list (filtered to the module).
        var conversations = await client.GetFromJsonAsync<JsonElement>("/api/chat/conversations?moduleId=finance");
        Assert.Contains(conversations.EnumerateArray(), c => c.GetProperty("id").GetString() == conversationId);

        // Its messages read back — including the user's turn, tagged with role "User".
        var messages = await client.GetFromJsonAsync<JsonElement>($"/api/chat/conversations/{conversationId}/messages");
        Assert.Contains(messages.EnumerateArray(), m =>
            m.GetProperty("role").GetString() == "User"
            && (m.GetProperty("content").GetString() ?? "").Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConversationMessages_AreScopedToTheOwningUser()
    {
        var owner = fixture.ClientFor("system_admin");
        var threadId = "iso-" + Guid.NewGuid().ToString("N")[..8];
        var conversationId = ConversationIdOf(await RunAguiTurnAsync(owner, threadId, "Hello there"));
        Assert.False(string.IsNullOrEmpty(conversationId));

        // A different user (own row, even as system_admin) cannot read another user's conversation history.
        var other = fixture.Factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Dev-Subject", "history-other-user");
        other.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        other.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");

        using var response = await other.GetAsync($"/api/chat/conversations/{conversationId}/messages");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RenamingAConversation_UpdatesItsTitle_AndIsOwnerScoped()
    {
        var client = fixture.ClientFor("system_admin");
        var threadId = "ren-" + Guid.NewGuid().ToString("N")[..8];
        var conversationId = ConversationIdOf(await RunAguiTurnAsync(client, threadId, "Hello there"));
        Assert.False(string.IsNullOrEmpty(conversationId));

        var newTitle = "Quarterly review " + Guid.NewGuid().ToString("N")[..6];
        using (var rename = await client.PutAsJsonAsync($"/api/chat/conversations/{conversationId}/title", new { title = newTitle }))
        {
            Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        }

        // The new title shows up in the user's conversation list.
        var conversations = await client.GetFromJsonAsync<JsonElement>("/api/chat/conversations?moduleId=finance");
        Assert.Contains(conversations.EnumerateArray(),
            c => c.GetProperty("id").GetString() == conversationId && c.GetProperty("title").GetString() == newTitle);

        // An empty title is rejected, and a different user can't rename it.
        using (var empty = await client.PutAsJsonAsync($"/api/chat/conversations/{conversationId}/title", new { title = "  " }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }
        var other = fixture.Factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Dev-Subject", "rename-other-user");
        other.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        other.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        using (var forbidden = await other.PutAsJsonAsync($"/api/chat/conversations/{conversationId}/title", new { title = "hijack" }))
        {
            Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        }
    }

    [Fact]
    public async Task DeletingAConversation_RemovesIt_AndIsOwnerScoped()
    {
        var client = fixture.ClientFor("system_admin");
        var threadId = "del-" + Guid.NewGuid().ToString("N")[..8];
        var conversationId = ConversationIdOf(await RunAguiTurnAsync(client, threadId, "Hello there"));
        Assert.False(string.IsNullOrEmpty(conversationId));

        // A different user cannot delete it.
        var other = fixture.Factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Dev-Subject", "delete-other-user");
        other.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        other.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");
        using (var forbidden = await other.DeleteAsync($"/api/chat/conversations/{conversationId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        }

        // The owner deletes it…
        using (var deleted = await client.DeleteAsync($"/api/chat/conversations/{conversationId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        // …and it's gone: its messages 404 and it's no longer listed.
        using (var gone = await client.GetAsync($"/api/chat/conversations/{conversationId}/messages"))
        {
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
        var list = await client.GetFromJsonAsync<JsonElement>("/api/chat/conversations?moduleId=finance");
        Assert.DoesNotContain(list.EnumerateArray(), c => c.GetProperty("id").GetString() == conversationId);
    }

    [Fact]
    public async Task ChatEndpoint_IsRateLimited_PerUser()
    {
        await using var factory = new RateLimitedFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "ratelimit-probe");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");

        var body = new { messages = new[] { new { role = "user", content = "Hi" } } };

        // This host caps chat at 2 turns/user/minute — the first two are admitted.
        for (var i = 0; i < 2; i++)
        {
            using var ok = await client.PostAsJsonAsync("/api/agui/finance", body);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            await ok.Content.ReadAsStringAsync();
        }

        // …the third exceeds the per-user limit and is rejected (with a Retry-After hint), before the agent runs.
        using var limited = await client.PostAsJsonAsync("/api/agui/finance", body);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    /// <summary>POSTs one AG-UI turn to the finance endpoint under a client-owned thread id; returns the SSE body.</summary>
    private static async Task<string> RunAguiTurnAsync(HttpClient http, string threadId, string content)
    {
        using var resp = await http.PostAsJsonAsync("/api/agui/finance",
            new { threadId, messages = new[] { new { role = "user", content } } });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>Pulls the conversation id out of an AG-UI SSE stream's RUN_FINISHED result frame.</summary>
    private static string? ConversationIdOf(string sse)
    {
        foreach (var line in sse.Split('\n'))
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains("RUN_FINISHED", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(line["data: ".Length..]);
                return doc.RootElement.GetProperty("result").GetProperty("conversationId").GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Hosts the sample API in the <c>Production</c> environment (reusing the fixture's throwaway Postgres via
    /// the connection-string env vars it sets). Entra config is supplied so auth is "configured" exactly as in
    /// a real deploy — no OIDC metadata is fetched (that happens lazily, only on protected-endpoint token
    /// validation), so the anonymous health endpoints behave as they would in production.
    /// </summary>
    private sealed class ProductionApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Auth:Authority", "https://login.example.com/00000000-0000-0000-0000-000000000000/v2.0");
            builder.UseSetting("Auth:Audience", "api://plenipo-tests");
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(Path.GetTempPath(), $"plenipo-dp-{Guid.NewGuid():N}"));
        }
    }

    /// <summary>A second Development host over the fixture's (already-seeded) Postgres — exercises a restart.</summary>
    private sealed class DevelopmentRestartFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
    }

    /// <summary>A Development host with a deliberately low chat rate limit, to exercise the 429 path.</summary>
    private sealed class RateLimitedFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("RateLimiting:ChatPermitsPerMinute", "2");
        }
    }
}

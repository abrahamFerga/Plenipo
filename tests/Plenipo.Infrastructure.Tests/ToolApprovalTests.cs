using Plenipo.Application.Auditing;
using Plenipo.Application.Usage;
using Plenipo.Core.Identity;
using Plenipo.Infrastructure.Agents;
using Plenipo.Modules.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// Verifies human-in-the-loop enforcement: a side-effecting tool (manifest <c>RequiresApproval</c>) is
/// blocked by <see cref="ToolInvocationMiddleware"/> and never executed, while a normal tool runs. Driven
/// by a fake tool-calling <see cref="IChatClient"/> through a real MAF agent — no real LLM needed.
/// </summary>
public sealed class ToolApprovalTests
{
    [Fact]
    public async Task ApprovalRequiredTool_IsBlocked_AndNotExecuted()
    {
        var (agent, middleware, wasExecuted) = BuildAgent(callTool: "danger", toolName: "danger", requiresApproval: true);

        await agent.RunAsync("do the dangerous thing");

        Assert.False(wasExecuted());                      // the tool's body never ran
        Assert.Contains(middleware.BlockedForApproval, b => b.ToolName == "danger");
    }

    [Fact]
    public async Task NonApprovalTool_Executes()
    {
        var (agent, middleware, wasExecuted) = BuildAgent(callTool: "safe", toolName: "safe", requiresApproval: false);

        await agent.RunAsync("do the safe thing");

        Assert.True(wasExecuted());
        Assert.Empty(middleware.BlockedForApproval);
    }

    private static (AIAgent Agent, ToolInvocationMiddleware Middleware, Func<bool> WasExecuted) BuildAgent(
        string callTool, string toolName, bool requiresApproval)
    {
        var executed = false;
        var tool = AIFunctionFactory.Create(() => { executed = true; return "executed"; }, name: toolName);

        var approval = new HashSet<string>(StringComparer.Ordinal);
        if (requiresApproval)
        {
            approval.Add(toolName);
        }

        var middleware = new ToolInvocationMiddleware(
            new NoopAuditLog(),
            new FakeCurrentUser(),
            approval,
            new Dictionary<string, ModuleTool>(),
            moduleId: "demo",
            conversationId: Guid.NewGuid());

        var agent = new ToolCallingChatClient(callTool)
            .AsBuilder()
            .BuildAIAgent(instructions: "test", tools: new List<AITool> { tool })
            .AsBuilder()
            .Use(middleware.InvokeAsync)
            .Build();

        return (agent, middleware, () => executed);
    }

    /// <summary>Issues a single tool call on the first response, then plain text — terminating the loop.</summary>
    private sealed class ToolCallingChatClient(string toolToCall) : IChatClient
    {
        private int _turn;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _turn++;
            if (_turn == 1)
            {
                var call = new FunctionCallContent("call-1", toolToCall, new Dictionary<string, object?>());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent> { call })));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "All done.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class NoopAuditLog : IAuditLog
    {
        public Task RecordToolCallAsync(ToolCallAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordAuthEventAsync(AuthAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordEntityChangesAsync(IReadOnlyCollection<EntityChangeAuditEntry> entries, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordTokenUsageAsync(TokenUsageRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? UserId => Guid.Empty;
        public string? Subject => "test";
        public string? DisplayName => "Test User";
        public Guid? TenantId => Guid.Empty;
        public bool IsAuthenticated => true;
        public IReadOnlySet<string> Permissions => new HashSet<string>();
        public bool HasPermission(string permission) => true;
    }
}

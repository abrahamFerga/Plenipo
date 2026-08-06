using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Plenipo.Application.Ai;
using Plenipo.Application.Auditing;
using Plenipo.Application.Usage;
using Plenipo.Core.Identity;
using Plenipo.Infrastructure.Agents;
using Plenipo.Modules.Sdk;

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

    [Fact]
    public async Task EnforcedSensitiveDataPolicy_RedactsToolArgumentsBeforeExecution()
    {
        string? received = null;
        var tool = AIFunctionFactory.Create(
            (string recipient) =>
            {
                received = recipient;
                return "executed";
            },
            name: "send");
        var policy = EffectiveAgentSecurityPolicy.Disabled with
        {
            Mode = AgentSecurityMode.Enforce,
            SensitiveDataHandling = SensitiveDataHandling.Redact,
        };
        var middleware = new ToolInvocationMiddleware(
            new NoopAuditLog(),
            new FakeCurrentUser(),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, ModuleTool>(),
            moduleId: "demo",
            conversationId: Guid.NewGuid(),
            agentSecurity: new RedactingSecurityService(),
            securityPolicy: policy);
        var agent = new ToolCallingChatClient(
                "send",
                new Dictionary<string, object?> { ["recipient"] = "alice@example.com" })
            .AsBuilder()
            .BuildAIAgent(instructions: "test", tools: new List<AITool> { tool })
            .AsBuilder()
            .Use(middleware.InvokeAsync)
            .Build();

        await agent.RunAsync("send a message");

        Assert.Equal("[REDACTED:EMAIL]", received);
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
    private sealed class ToolCallingChatClient(
        string toolToCall,
        Dictionary<string, object?>? arguments = null) : IChatClient
    {
        private int _turn;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _turn++;
            if (_turn == 1)
            {
                var call = new FunctionCallContent(
                    "call-1",
                    toolToCall,
                    arguments ?? new Dictionary<string, object?>());
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

    private sealed class RedactingSecurityService : IAgentSecurityService
    {
        public bool HarmfulContentDetectionConfigured => false;

        public Task<AgentSecurityInspection> InspectAsync(
            string text,
            AgentSecurityStage stage,
            EffectiveAgentSecurityPolicy policy,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentSecurityInspection
            {
                Text = text.Replace("alice@example.com", "[REDACTED:EMAIL]", StringComparison.Ordinal),
                Modified = stage == AgentSecurityStage.ToolInput &&
                    text.Contains("alice@example.com", StringComparison.Ordinal),
            });
    }

    private sealed class NoopAuditLog : IAuditLog
    {
        public Task RecordToolCallAsync(ToolCallAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordAuthEventAsync(AuthAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordEntityChangesAsync(IReadOnlyCollection<EntityChangeAuditEntry> entries, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordTokenUsageAsync(TokenUsageRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RecordAgentRunAsync(AgentRunRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

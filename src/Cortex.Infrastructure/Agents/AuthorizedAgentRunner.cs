using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cortex.Application.Agents;
using Cortex.Application.Ai;
using Cortex.Application.Approvals;
using Cortex.Application.Auditing;
using Cortex.Application.Connectors;
using Cortex.Application.Conversations;
using Cortex.Application.Modules;
using Cortex.Application.Skills;
using Cortex.Application.Usage;
using Cortex.Core.Identity;
using Cortex.Core.Platform;
using Cortex.Modules.Sdk;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cortex.Infrastructure.Agents;

/// <summary>
/// The security spine of the chat surface. For each turn it:
/// <list type="number">
///   <item>resolves the module's tools and keeps only those the user is permitted to call — the model
///   never receives the schema of a forbidden tool;</item>
///   <item>wraps tool dispatch in audit middleware so every invocation is recorded;</item>
///   <item>resumes the conversation's MAF <see cref="AgentSession"/> (persisted per conversation) and
///   streams the assistant's reply back.</item>
/// </list>
/// </summary>
public sealed class AuthorizedAgentRunner(
    IServiceProvider services,
    IToolRegistry toolRegistry,
    IConnectorToolCatalog connectorTools,
    IModuleCatalog moduleCatalog,
    ITenantModuleStore tenantModuleStore,
    ITenantAiSettings tenantAiSettings,
    ITenantChatClientResolver chatClients,
    IAgentProfileResolver agentProfiles,
    IInstructionSnapshotStore instructionSnapshots,
    IConversationStore conversations,
    IAuditLog auditLog,
    ITokenUsageReader usageReader,
    Cortex.Infrastructure.Usage.BudgetAlerts budgetAlerts,
    IApprovalStore approvalStore,
    Cortex.Infrastructure.Approvals.ApprovalNotifier approvalNotifier,
    ISkillCatalog skillCatalog,
    ICurrentUser currentUser,
    IOptions<AiOptions> aiOptions,
    ILogger<AuthorizedAgentRunner> logger) : IAuthorizedAgentRunner
{
    private readonly AiOptions _ai = aiOptions.Value;

    /// <summary>
    /// Serializer options for the persisted <see cref="AgentSession"/> state: the MAF agent-abstractions
    /// options (polymorphic AIContent contracts) plus out-of-order metadata tolerance, because the state
    /// lives in a PostgreSQL <c>jsonb</c> column and jsonb does not preserve key order — the <c>$type</c>
    /// discriminator can come back after other properties.
    /// </summary>
    private static readonly JsonSerializerOptions SessionStateJson = new(AgentAbstractionsJsonUtilities.DefaultOptions)
    {
        AllowOutOfOrderMetadataProperties = true,
    };

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!moduleCatalog.TryGetManifest(request.ModuleId, out var manifest) || manifest is null)
        {
            yield return AgentStreamEvent.Failed($"Unknown module '{request.ModuleId}'.");
            yield break;
        }

        // A module disabled for this tenant is not just hidden from the workspace — it's uninvocable. Refuse
        // the turn before resolving any tools, so a disabled module's tools never reach the model.
        if (!await tenantModuleStore.IsEnabledAsync(request.ModuleId, cancellationToken))
        {
            yield return AgentStreamEvent.Failed($"The '{manifest.DisplayName}' module is not enabled for this tenant.");
            yield break;
        }

        // Resolve this tenant's effective AI settings — system prompt, budgets, AND the provider
        // connection (a tenant may run its own provider + vaulted key; SaaS bring-your-own-key).
        var aiSettings = await tenantAiSettings.ResolveAsync(cancellationToken);

        // A picked name may be a module WORKFLOW: a sequential chain of the module's agents. Each
        // step runs as a full authorized turn through this very method (same RBAC, budgets,
        // approvals, audit), with the previous step's output handed to the next. Steps must be
        // agents — workflows never nest.
        if (!string.IsNullOrWhiteSpace(request.Agent))
        {
            var workflow = manifest.Workflows.FirstOrDefault(
                w => string.Equals(w.Name, request.Agent, StringComparison.Ordinal));
            if (workflow is not null)
            {
                if (workflow.AgentNames.Count == 0 ||
                    workflow.AgentNames.Any(n => manifest.Workflows.Any(w => string.Equals(w.Name, n, StringComparison.Ordinal))))
                {
                    yield return AgentStreamEvent.Failed(
                        $"Workflow '{workflow.Name}' is misdeclared: it needs at least one step, and steps must be agents, not workflows.");
                    yield break;
                }

                await foreach (var evt in RunWorkflowAsync(workflow, request, cancellationToken))
                {
                    yield return evt;
                }

                yield break;
            }
        }

        // The agent for this turn: the one the user picked by name (tenant profile or manifest
        // agent — never a silent fallback when the name is unknown), else the default. Either way
        // it retasks or specializes the chatbot — different voice/policy, its own model, and (when
        // it declares a tool selection) a narrower tool surface. Resolved before tool filtering.
        AgentProfile? profile;
        if (!string.IsNullOrWhiteSpace(request.Agent))
        {
            profile = await agentProfiles.ResolveNamedAsync(request.ModuleId, request.Agent, cancellationToken);
            if (profile is null)
            {
                yield return AgentStreamEvent.Failed(
                    $"Unknown agent '{request.Agent}' for the '{manifest.DisplayName}' module.");
                yield break;
            }
        }
        else
        {
            profile = await agentProfiles.ResolveActiveAsync(request.ModuleId, cancellationToken);
        }

        // Per-turn model pick (Claude-Code-style): honoured only from the advertised list, so a
        // client can never steer the turn onto an arbitrary model string. Beats the agent's pin.
        string? modelOverride = profile?.Model;
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            if (!aiSettings.AllowsModel(request.Model))
            {
                yield return AgentStreamEvent.Failed(
                    $"Model '{request.Model}' is not available. An administrator configures the selectable models (Ai:AvailableModels).");
                yield break;
            }

            modelOverride = request.Model;
        }

        // The turn's chat client: the tenant's connection with the model override. A misconfigured
        // connection (e.g. a key that no longer reveals) fails the turn readably.
        IChatClient? chatClient = null;
        string? clientError = null;
        try
        {
            chatClient = await chatClients.ResolveAsync(aiSettings, modelOverride, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not build the chat client for provider {Provider}.", aiSettings.Provider);
            clientError = "The AI provider connection is misconfigured for this tenant. An administrator can fix it under AI settings.";
        }

        if (clientError is not null)
        {
            yield return AgentStreamEvent.Failed(clientError);
            yield break;
        }

        if (chatClient is null)
        {
            yield return AgentStreamEvent.Failed("The AI provider is not configured for this deployment.");
            yield break;
        }

        // --- Pre-model-call tool filtering: the model only ever sees tools the caller may invoke. ---
        // Module + platform tools, plus tools from connectors this TENANT has enabled (default-off) —
        // then every tool, whatever its source, passes the same per-permission gate. The profile's
        // tool selection intersects AFTER RBAC: it can hide a permitted tool, never grant one.
        var candidateTools = new List<ModuleTool>(toolRegistry.GetModuleTools(request.ModuleId, services));
        candidateTools.AddRange(await connectorTools.GetEnabledToolsAsync(services, cancellationToken));

        var permittedTools = new List<ModuleTool>();
        foreach (var tool in candidateTools)
        {
            if (currentUser.HasPermission(tool.Permission) && AgentToolSelection.Matches(profile?.ToolNames, tool.Name))
            {
                permittedTools.Add(tool);
            }
        }

        var toolsByName = permittedTools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var aiTools = permittedTools.Select(t => (AITool)t.Function).ToList();

        // Resolve or create the conversation, then load its history. A supplied id is found-or-created so a
        // client that owns its thread id (AG-UI) gets continuity on the first turn instead of "not found".
        var conversation = request.ConversationId is { } id
            ? await conversations.GetOrCreateAsync(id, request.ModuleId, cancellationToken)
            : await conversations.CreateAsync(request.ModuleId, cancellationToken);

        if (conversation is null)
        {
            yield return AgentStreamEvent.Failed("Conversation not found.");
            yield break;
        }

        // Per-conversation token budget (per-tenant override applied): refuse the turn once prior usage has reached the cap.
        if (aiSettings.MaxConversationTokens > 0)
        {
            var consumed = await usageReader.GetConversationTotalAsync(conversation.Id, cancellationToken);
            if (TokenBudget.IsExceeded(consumed, aiSettings.MaxConversationTokens))
            {
                yield return AgentStreamEvent.Failed(
                    $"This conversation has reached its token budget ({aiSettings.MaxConversationTokens:N0} tokens). Start a new conversation to continue.");
                yield break;
            }
        }

        // Tenant-wide monthly budget: the org-level cost ceiling. The pre-turn total also feeds the
        // post-turn threshold-crossing alerts (80% warning / exhaustion) to the tenant's admins.
        long monthConsumed = 0;
        if (aiSettings.MaxMonthlyTokens > 0)
        {
            monthConsumed = await usageReader.GetTenantMonthTotalAsync(DateTimeOffset.UtcNow, cancellationToken);
            if (TokenBudget.IsExceeded(monthConsumed, aiSettings.MaxMonthlyTokens))
            {
                yield return AgentStreamEvent.Failed(
                    $"This organization has reached its monthly token budget ({aiSettings.MaxMonthlyTokens:N0} tokens). An administrator can raise it under AI settings.");
                yield break;
            }
        }

        // Slash invocation (Claude-Code-style): a message starting with /skill-name — matched
        // against the skills ADVERTISED for this module — becomes an explicit load-and-follow
        // instruction. Anything else starting with '/' passes through untouched.
        var message = request.Message;
        if (skillCatalog.IsEnabled && message.StartsWith('/') && message.Length > 1)
        {
            var parts = message[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
            var slashSkill = skillCatalog.List(request.ModuleId)
                .FirstOrDefault(s => string.Equals(s.Name, parts[0], StringComparison.OrdinalIgnoreCase));
            if (slashSkill is not null)
            {
                var rest = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "Apply it to this conversation now.";
                message = $"Load the '{slashSkill.Name}' skill with load_skill and follow its instructions for this request: {rest}";
            }
        }

        // Tools marked side-effecting are blocked pending human approval — both the module
        // manifest's declarations and per-tool flags on platform/connector tools (connector fetch
        // tools and skill scripts carry the flag on the ModuleTool itself, not in a manifest).
        var approvalRequired = manifest.Tools
            .Where(t => t.RequiresApproval)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        approvalRequired.UnionWith(candidateTools.Where(t => t.RequiresApproval).Select(t => t.Name));

        var instructions = InstructionComposer.Compose(aiSettings.SystemPrompt, manifest.AgentInstructions, profile);

        // Advertise skills (name + description only — progressive disclosure) when this user can
        // actually load them; the full instructions arrive via the load_skill tool on demand.
        if (skillCatalog.IsEnabled && toolsByName.ContainsKey("load_skill"))
        {
            instructions = SkillAdvertisement.Append(instructions, skillCatalog.List(request.ModuleId));
        }

        // Provenance: pin the exact instruction assembly this turn runs under. The snapshot store
        // is best-effort and never fails the turn; the hash lands on the assistant message below.
        var instructionsHash = InstructionHash.Compute(instructions);
        await instructionSnapshots.EnsureAsync(instructionsHash, instructions, cancellationToken);
        var middleware = new ToolInvocationMiddleware(auditLog, currentUser, approvalRequired, toolsByName, request.ModuleId, conversation.Id);

        // Instrument the chat client (LLM calls + token usage) and the agent (runs) so the whole turn is
        // traced under the Cortex.Agents OpenTelemetry source and shows up in the Aspire dashboard.
        var tracedChatClient = chatClient.AsBuilder()
            .UseOpenTelemetry(sourceName: AgentTelemetry.SourceName)
            .Build();
        var agent = tracedChatClient.AsBuilder()
            .BuildAIAgent(instructions: instructions, tools: aiTools)
            .AsBuilder()
            .Use(middleware.InvokeAsync)
            .UseOpenTelemetry(sourceName: AgentTelemetry.SourceName)
            .Build();

        // Resume the conversation's MAF session (framework-owned state: full history including tool
        // calls/results), persisted per conversation. Conversations from before session support — or
        // whose state fails to round-trip — fall back to seeding a fresh session with the replayed
        // user/assistant history, exactly the pre-session behaviour.
        // The model gets the (possibly slash-rewritten) message; the transcript below persists what
        // the user actually typed.
        var session = await ResumeSessionAsync(agent, conversation, cancellationToken);
        IReadOnlyList<ChatMessage> turnInput = session.Resumed
            ? [new ChatMessage(ChatRole.User, message)]
            : BuildHistory(conversation, message);

        var runOptions = new ChatClientAgentRunOptions(new ChatOptions
        {
            Temperature = _ai.Temperature,
            MaxOutputTokens = _ai.MaxOutputTokens,
        });

        var assistant = new StringBuilder();
        var announcedTools = new HashSet<string>(StringComparer.Ordinal);
        var usage = new UsageAccumulator();

        await foreach (var evt in StreamTurnAsync(agent, turnInput, session.Session, runOptions, assistant, announcedTools, usage, cancellationToken))
        {
            yield return evt;
            if (evt.Type == AgentStreamEventType.Error)
            {
                yield break;
            }
        }

        var sessionState = await SerializeSessionAsync(agent, session.Session, cancellationToken);
        await conversations.AppendTurnAsync(conversation.Id, request.Message, assistant.ToString(), sessionState, instructionsHash, cancellationToken);
        await RecordUsageAsync(request.ModuleId, conversation.Id, usage,
            aiSettings.Provider, profile?.Model is { Length: > 0 } m ? m : aiSettings.Model, cancellationToken);

        if (aiSettings.MaxMonthlyTokens > 0 && usage.HasAny)
        {
            await budgetAlerts.NotifyCrossingsAsync(
                monthConsumed, monthConsumed + usage.Effective, aiSettings.MaxMonthlyTokens, cancellationToken);
        }

        // Persist any blocked side-effecting tool calls as pending approvals, then surface them to the client.
        foreach (var blocked in middleware.BlockedForApproval)
        {
            var pending = new PendingApproval
            {
                TenantId = currentUser.TenantId ?? Guid.Empty,
                UserId = currentUser.UserId,
                UserDisplay = currentUser.DisplayName,
                ConversationId = conversation.Id,
                ModuleId = request.ModuleId,
                ToolName = blocked.ToolName,
                ArgumentsJson = blocked.ArgumentsJson,
            };
            await approvalStore.RecordPendingAsync(pending, cancellationToken);

            // Ping everyone who can act on it (never throws — see ApprovalNotifier): approvers
            // should learn about blocked actions from their inbox, not by camping in this chat.
            await approvalNotifier.NotifyPendingAsync(pending, cancellationToken);

            yield return AgentStreamEvent.NeedsApproval(blocked.ToolName);
        }

        if (usage.HasAny)
        {
            yield return AgentStreamEvent.UsageReport(usage.InputTokens, usage.OutputTokens, usage.Effective);
        }

        yield return AgentStreamEvent.Completed(conversation.Id);
    }

    /// <summary>Persists the turn's token consumption to the audit store (best-effort; never throws).</summary>
    private async Task RecordUsageAsync(
        string moduleId, Guid conversationId, UsageAccumulator usage,
        string provider, string model, CancellationToken cancellationToken)
    {
        if (!usage.HasAny)
        {
            // The provider reported no usage for this turn (e.g. Ollama, or streaming usage disabled).
            return;
        }

        // Provider/model are the EFFECTIVE ones for the turn (tenant connection + profile model),
        // so per-customer usage reports attribute spend to what actually ran.
        await auditLog.RecordTokenUsageAsync(new TokenUsageRecord
        {
            TenantId = currentUser.TenantId ?? Guid.Empty,
            UserId = currentUser.UserId,
            UserDisplay = currentUser.DisplayName,
            ModuleId = moduleId,
            ConversationId = conversationId,
            Provider = provider,
            Model = model,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            TotalTokens = usage.Effective,
        }, cancellationToken);
    }

    /// <summary>
    /// Deserializes the conversation's persisted <see cref="AgentSession"/>, or creates a fresh one.
    /// <c>Resumed</c> is false when the caller must seed the new session with replayed history.
    /// </summary>
    private async Task<(AgentSession Session, bool Resumed)> ResumeSessionAsync(
        AIAgent agent, Conversation conversation, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(conversation.SessionState))
        {
            try
            {
                using var state = JsonDocument.Parse(conversation.SessionState);
                var session = await agent.DeserializeSessionAsync(state.RootElement, SessionStateJson, cancellationToken);
                return (session, true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Corrupt or incompatible state (e.g. after a framework upgrade) must not brick the
                // conversation — fall back to history replay and write fresh state after this turn.
                logger.LogWarning(ex, "Could not deserialize agent session for conversation {ConversationId}; replaying history.", conversation.Id);
            }
        }

        return (await agent.CreateSessionAsync(cancellationToken), false);
    }

    /// <summary>Serializes the session for persistence (best-effort — a failure falls back to history replay next turn).</summary>
    private async Task<string?> SerializeSessionAsync(AIAgent agent, AgentSession session, CancellationToken cancellationToken)
    {
        try
        {
            var element = await agent.SerializeSessionAsync(session, SessionStateJson, cancellationToken);
            return JsonSerializer.Serialize(element);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not serialize agent session; the conversation will resume by history replay.");
            return null;
        }
    }

    private async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(
        AIAgent agent,
        IReadOnlyList<ChatMessage> messages,
        AgentSession session,
        AgentRunOptions runOptions,
        StringBuilder assistant,
        HashSet<string> announcedTools,
        UsageAccumulator usage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var enumerator = agent.RunStreamingAsync(messages, session, options: runOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                AgentResponseUpdate? update = null;
                string? error = null;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Agent turn failed");
                    error = "The assistant could not complete the request.";
                }

                if (error is not null)
                {
                    yield return AgentStreamEvent.Failed(error);
                    yield break;
                }

                foreach (var call in update!.Contents.OfType<FunctionCallContent>())
                {
                    if (announcedTools.Add(call.CallId))
                    {
                        yield return AgentStreamEvent.ToolInvoked(call.Name);
                    }
                }

                // Capture token usage the provider reports (typically on the final streamed update).
                foreach (var usageContent in update.Contents.OfType<UsageContent>())
                {
                    usage.Add(usageContent.Details);
                }

                if (!string.IsNullOrEmpty(update.Text))
                {
                    assistant.Append(update.Text);
                    yield return AgentStreamEvent.Token(update.Text);
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }


    /// <summary>
    /// Sequential workflow execution by composition: each step is a normal <see cref="RunAsync"/>
    /// turn in the SAME conversation, so every platform guarantee applies per step and the
    /// transcript shows the full chain (handoffs included). Only the last step's Completed event
    /// reaches the client; a failed step fails the workflow rather than running the next blind.
    /// </summary>
    private async IAsyncEnumerable<AgentStreamEvent> RunWorkflowAsync(
        WorkflowDescriptor workflow,
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Guid? conversationId = request.ConversationId;
        var previousOutput = new StringBuilder();

        for (var i = 0; i < workflow.AgentNames.Count; i++)
        {
            var stepName = workflow.AgentNames[i];
            var stepMessage = i == 0
                ? request.Message
                : $"Workflow '{workflow.Name}', step {i + 1} of {workflow.AgentNames.Count} — do your part.\n" +
                  $"Original request: {request.Message}\n" +
                  $"Output of the prior step:\n{previousOutput}";
            previousOutput.Clear();

            var step = request with { Agent = stepName, Message = stepMessage, ConversationId = conversationId };
            if (workflow.AgentNames.Count > 1)
            {
                yield return AgentStreamEvent.Token($"{(i == 0 ? "" : "\n\n")}**{stepName}** ({i + 1}/{workflow.AgentNames.Count}):\n\n");
            }

            var failed = false;
            await foreach (var evt in RunAsync(step, cancellationToken))
            {
                if (evt.Type == AgentStreamEventType.Token && evt.Text is not null)
                {
                    previousOutput.Append(evt.Text);
                }

                if (evt.Type == AgentStreamEventType.Completed)
                {
                    // Chain all steps into ONE conversation; only the last step completes the run.
                    conversationId = evt.ConversationId ?? conversationId;
                    if (i == workflow.AgentNames.Count - 1)
                    {
                        yield return evt;
                    }
                    continue;
                }

                failed |= evt.Type == AgentStreamEventType.Error;
                yield return evt;
            }

            if (failed)
            {
                yield break;
            }
        }
    }

    private static List<ChatMessage> BuildHistory(Conversation conversation, string newMessage)
    {
        var messages = new List<ChatMessage>();
        foreach (var message in conversation.Messages.OrderBy(m => m.CreatedAt))
        {
            var role = message.Role == MessageRole.Assistant ? ChatRole.Assistant : ChatRole.User;
            messages.Add(new ChatMessage(role, message.Content));
        }

        messages.Add(new ChatMessage(ChatRole.User, newMessage));
        return messages;
    }

    /// <summary>Accumulates token usage across the (possibly multi-call) updates of a single turn.</summary>
    private sealed class UsageAccumulator
    {
        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public long TotalTokens { get; private set; }

        /// <summary>True when the provider reported any usage for the turn.</summary>
        public bool HasAny => TokenTotals.Any(InputTokens, OutputTokens, TotalTokens);

        /// <summary>The billed total: a reported total when present, else the sum of the parts.</summary>
        public long Effective => TokenTotals.Effective(InputTokens, OutputTokens, TotalTokens);

        public void Add(UsageDetails? details)
        {
            if (details is null)
            {
                return;
            }

            InputTokens += details.InputTokenCount ?? 0;
            OutputTokens += details.OutputTokenCount ?? 0;
            TotalTokens += details.TotalTokenCount ?? 0;
        }
    }
}

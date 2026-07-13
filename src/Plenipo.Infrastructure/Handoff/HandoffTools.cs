using Plenipo.Application.Agents;
using Plenipo.Application.Ai;
using Plenipo.Application.Modules;
using Plenipo.Core.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Infrastructure.Handoff;

/// <summary>
/// Cross-module handoff INSIDE one host: "ask finance" from the legal chat runs a nested,
/// one-shot agent turn against the target module — same user, same tenant, same RBAC tool
/// filtering. Deliberately narrower than a full chat turn:
/// <list type="bullet">
///   <item>READ-ONLY — approval-required tools never reach the nested agent, so a handoff can
///   answer questions but not take actions;</item>
///   <item>NON-RECURSIVE — handoff tools are excluded from the nested tool set, and an
///   AsyncLocal depth guard backstops the exclusion;</item>
///   <item>STATELESS — no conversation row, no session; the answer text returns to the calling
///   agent, which owns the conversation.</item>
/// </list>
/// The cross-SYSTEM case (separate deployments) stays with the plenipo-peer connector.
/// </summary>
public sealed class HandoffTools(
    IServiceProvider services,
    IModuleCatalog moduleCatalog,
    ITenantModuleStore tenantModules,
    IToolRegistry toolRegistry,
    ITenantAiSettings tenantAiSettings,
    ITenantChatClientResolver chatClients,
    IAgentProfileResolver agentProfiles,
    ICurrentUser currentUser)
{
    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>Hard cap on the relayed answer, so one handoff can't flood the outer context.</summary>
    public const int MaxAnswerLength = 4000;

    public async Task<string> AskModuleAsync(string targetModuleId, string question, CancellationToken cancellationToken)
    {
        if (Depth.Value > 0)
        {
            return "Handoff is not available from within a handoff — answer with the tools you have.";
        }

        if (!moduleCatalog.TryGetManifest(targetModuleId, out var manifest) || manifest is null)
        {
            return $"Unknown module '{targetModuleId}'.";
        }

        if (!await tenantModules.IsEnabledAsync(targetModuleId, cancellationToken))
        {
            return $"The '{manifest.DisplayName}' module is not enabled for this tenant.";
        }

        // The nested tool set: the target module's tools the CALLER may use, minus anything
        // side-effecting (manifest- or tool-flagged) and minus the handoff tools themselves.
        var approvalRequired = manifest.Tools
            .Where(t => t.RequiresApproval)
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        var tools = toolRegistry.GetModuleTools(targetModuleId, services)
            .Where(t => t.ModuleId != Plenipo.Application.Authorization.Permissions.HandoffToolModule)
            .Where(t => !t.RequiresApproval && !approvalRequired.Contains(t.Name))
            .Where(t => currentUser.HasPermission(t.Permission))
            .Select(t => (AITool)t.Function)
            .ToList();

        var aiSettings = await tenantAiSettings.ResolveAsync(cancellationToken);
        var profile = await agentProfiles.ResolveActiveAsync(targetModuleId, cancellationToken);

        // The nested turn runs on the same tenant connection (and the target module's profile
        // model) as a direct chat with that module would.
        IChatClient? chatClient;
        try
        {
            chatClient = await chatClients.ResolveAsync(aiSettings, profile?.Model, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "The AI provider connection is misconfigured for this tenant.";
        }

        if (chatClient is null)
        {
            return "The AI provider is not configured for this deployment.";
        }

        var instructions =
            InstructionComposer.Compose(aiSettings.SystemPrompt, manifest.AgentInstructions, profile) +
            "\n\nYou are answering a single question relayed from another assistant on the user's behalf. " +
            "Answer concisely from the data your tools can read; you cannot take actions.";

        var agent = chatClient.AsBuilder().BuildAIAgent(instructions: instructions, tools: tools);

        Depth.Value++;
        try
        {
            var response = await agent.RunAsync(question, cancellationToken: cancellationToken);
            var text = response.Text.Trim();
            if (text.Length == 0)
            {
                return $"The {manifest.DisplayName} module had no answer.";
            }

            return text.Length <= MaxAnswerLength ? text : text[..MaxAnswerLength] + " …";
        }
        finally
        {
            Depth.Value--;
        }
    }
}

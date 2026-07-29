using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Plenipo.Application.Ai;

namespace Plenipo.Infrastructure.Agents.Security;

/// <summary>
/// Provider-neutral policy engine. Plenipo's deterministic sensitive-data and prompt-attack checks run
/// locally before any external call. Azure optionally adds a separately trained prompt-attack classifier
/// and harmful-content categories when configured.
/// </summary>
internal sealed class AgentSecurityService(
    AzureContentSafetyClient azure,
    ILogger<AgentSecurityService> logger) : IAgentSecurityService
{
    public bool HarmfulContentDetectionConfigured => azure.IsConfigured;

    public async Task<AgentSecurityInspection> InspectAsync(
        string text,
        AgentSecurityStage stage,
        EffectiveAgentSecurityPolicy policy,
        CancellationToken cancellationToken = default)
    {
        if (!policy.IsEnabled || string.IsNullOrEmpty(text))
        {
            return new AgentSecurityInspection { Text = text };
        }

        if (text.Length > policy.MaxInspectionCharacters)
        {
            var failClosed = policy.Mode == AgentSecurityMode.Enforce && policy.FailClosed;
            return new AgentSecurityInspection
            {
                Text = text,
                Blocked = failClosed,
                Unavailable = true,
                Findings = [new AgentSecurityFinding("Policy", "InspectionSizeExceeded")],
            };
        }

        var findings = new List<AgentSecurityFinding>();
        var protectedText = text;
        var sensitive = SensitiveDataDetector.DetectAndRedact(text);
        if (sensitive.Unavailable)
        {
            findings.Add(new AgentSecurityFinding("LocalSensitiveData", "Unavailable"));
            var failClosed = policy.Mode == AgentSecurityMode.Enforce && policy.FailClosed;
            return new AgentSecurityInspection
            {
                Text = text,
                Blocked = failClosed,
                Unavailable = true,
                Findings = findings,
            };
        }

        if (policy.SensitiveDataHandling != SensitiveDataHandling.Disabled && sensitive.HasMatches)
        {
            findings.AddRange(sensitive.Categories.Select(category =>
                new AgentSecurityFinding("LocalSensitiveData", category)));

            if (policy.Mode == AgentSecurityMode.Enforce)
            {
                if (policy.SensitiveDataHandling == SensitiveDataHandling.Block)
                {
                    return new AgentSecurityInspection
                    {
                        Text = text,
                        Blocked = true,
                        Findings = findings,
                    };
                }

                protectedText = sensitive.RedactedText;
            }
        }

        // Even in audit mode, keep locally recognized sensitive values out of the external classifier.
        var externalText = sensitive.HasMatches ? sensitive.RedactedText : text;
        var inspectPromptAttack = ShouldInspectPromptAttack(policy, stage);
        if (inspectPromptAttack)
        {
            var promptAttack = PlenipoPromptAttackDetector.Detect(externalText);
            findings.AddRange(promptAttack.Categories.Select(category =>
                new AgentSecurityFinding("PlenipoPromptGuard", category)));
            if (promptAttack.AttackDetected && policy.Mode == AgentSecurityMode.Enforce)
            {
                return new AgentSecurityInspection
                {
                    Text = protectedText,
                    Modified = !string.Equals(text, protectedText, StringComparison.Ordinal),
                    Blocked = true,
                    Findings = findings,
                };
            }
        }

        // Plenipo prompt-attack detection is always available in-process. Azure is required only for
        // harmful-content categories; when configured, its Prompt Shields classifier also runs as an
        // optional second opinion for prompt attacks.
        if (policy.ContentSafetyEnabled && !azure.IsConfigured)
        {
            findings.Add(new AgentSecurityFinding("AzureContentSafety", "Unavailable"));
            var failClosed = policy.Mode == AgentSecurityMode.Enforce && policy.FailClosed;
            return new AgentSecurityInspection
            {
                Text = protectedText,
                Modified = !string.Equals(text, protectedText, StringComparison.Ordinal),
                Blocked = failClosed,
                Unavailable = true,
                Findings = findings,
            };
        }

        var azureControlEnabled = policy.ContentSafetyEnabled || (inspectPromptAttack && azure.IsConfigured);
        try
        {
            var shieldTask = azureControlEnabled && inspectPromptAttack
                ? azure.DetectPromptAttackAsync(
                    externalText,
                    treatAsDocument: stage == AgentSecurityStage.ToolOutput,
                    cancellationToken)
                : Task.FromResult(false);

            var harmTask = azureControlEnabled && policy.ContentSafetyEnabled
                ? azure.AnalyzeHarmAsync(externalText, policy.HarmSeverityThreshold, cancellationToken)
                : Task.FromResult<IReadOnlyList<string>>([]);

            await Task.WhenAll(shieldTask, harmTask);

            if (await shieldTask)
            {
                findings.Add(new AgentSecurityFinding(
                    "AzureContentSafety",
                    stage == AgentSecurityStage.ToolOutput ? "IndirectPromptAttack" : "PromptAttack"));
            }

            findings.AddRange((await harmTask).Select(category =>
                new AgentSecurityFinding("AzureContentSafety", $"Harm:{category}")));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or AuthenticationFailedException
                                   or InvalidOperationException or TaskCanceledException)
        {
            // Never log inspected content or provider response bodies: both can contain PII or attack payloads.
            logger.LogWarning(ex, "Agent security inspection was unavailable at stage {Stage}.", stage);
            findings.Add(new AgentSecurityFinding("AzureContentSafety", "Unavailable"));
            var failClosed = policy.Mode == AgentSecurityMode.Enforce && policy.FailClosed;
            return new AgentSecurityInspection
            {
                Text = protectedText,
                Modified = !string.Equals(text, protectedText, StringComparison.Ordinal),
                Blocked = failClosed,
                Unavailable = true,
                Findings = findings,
            };
        }

        return new AgentSecurityInspection
        {
            Text = protectedText,
            Modified = !string.Equals(text, protectedText, StringComparison.Ordinal),
            Blocked = policy.Mode == AgentSecurityMode.Enforce &&
                findings.Any(f => f.Detector == "AzureContentSafety"),
            Findings = findings,
        };
    }

    private static bool ShouldInspectPromptAttack(EffectiveAgentSecurityPolicy policy, AgentSecurityStage stage) =>
        policy.PromptAttackDetectionEnabled &&
        stage is AgentSecurityStage.UserInput or AgentSecurityStage.ToolInput or AgentSecurityStage.ToolOutput;
}

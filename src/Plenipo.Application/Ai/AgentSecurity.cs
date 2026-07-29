using Plenipo.Core.Platform;

namespace Plenipo.Application.Ai;

/// <summary>How an agent-security policy affects a turn when a detector finds a risk.</summary>
public enum AgentSecurityMode
{
    /// <summary>No application-level screening. Provider-native filters can still apply.</summary>
    Disabled = 0,

    /// <summary>Run configured detectors and audit findings, but do not change or block content.</summary>
    Audit = 1,

    /// <summary>Run configured detectors and enforce their block/redaction decisions.</summary>
    Enforce = 2,
}

/// <summary>How locally detected PII, credentials, and other sensitive values are handled.</summary>
public enum SensitiveDataHandling
{
    Disabled = 0,
    Redact = 1,
    Block = 2,
}

/// <summary>The trust boundary at which content is being inspected.</summary>
public enum AgentSecurityStage
{
    UserInput = 0,
    ToolInput = 1,
    ToolOutput = 2,
    ModelOutput = 3,
}

/// <summary>
/// Deployment-level agent-security configuration. Tenant settings choose whether the controls are active;
/// the deployment owns the external safety service connection so tenants never handle its credential.
/// </summary>
public sealed class AgentSecurityOptions
{
    public const string SectionName = "AgentSecurity";

    /// <summary>
    /// Optional semantic detector augmentation: None or AzureContentSafety. Plenipo's deterministic
    /// prompt-attack and sensitive-data detectors do not require an external provider.
    /// </summary>
    public string Provider { get; set; } = "None";

    /// <summary>Azure AI Content Safety resource endpoint.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Optional Azure AI Content Safety key. Prefer managed identity by leaving this blank in production;
    /// when supplied, bind it from a secret environment/configuration source rather than a committed file.
    /// </summary>
    public string? ApiKey { get; set; }

    public AgentSecurityMode DefaultMode { get; set; } = AgentSecurityMode.Disabled;
    public bool PromptAttackDetectionEnabledByDefault { get; set; }
    public bool ContentSafetyEnabledByDefault { get; set; }
    public SensitiveDataHandling SensitiveDataHandlingByDefault { get; set; } = SensitiveDataHandling.Disabled;

    /// <summary>
    /// Azure Content Safety four-level severity that triggers a finding: 2=Low, 4=Medium, 6=High.
    /// </summary>
    public int HarmSeverityThreshold { get; set; } = 4;

    /// <summary>In Enforce mode, refuse guarded work when the external detector cannot make a decision.</summary>
    public bool FailClosed { get; set; } = true;

    /// <summary>Bound for text sent to a detector. Azure Content Safety currently accepts up to 100K chars.</summary>
    public int MaxInspectionCharacters { get; set; } = 100_000;

    public bool IsAzureContentSafetyConfigured =>
        string.Equals(Provider, "AzureContentSafety", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(Endpoint, UriKind.Absolute, out _);
}

/// <summary>The tenant's effective policy after deployment defaults and nullable overrides are merged.</summary>
public sealed record EffectiveAgentSecurityPolicy(
    AgentSecurityMode Mode,
    bool PromptAttackDetectionEnabled,
    bool ContentSafetyEnabled,
    SensitiveDataHandling SensitiveDataHandling,
    bool FailClosed,
    int HarmSeverityThreshold,
    int MaxInspectionCharacters)
{
    public static EffectiveAgentSecurityPolicy Disabled { get; } = new(
        AgentSecurityMode.Disabled,
        PromptAttackDetectionEnabled: false,
        ContentSafetyEnabled: false,
        SensitiveDataHandling.Disabled,
        FailClosed: true,
        HarmSeverityThreshold: 4,
        MaxInspectionCharacters: 100_000);

    public bool IsEnabled => Mode != AgentSecurityMode.Disabled &&
        (PromptAttackDetectionEnabled || ContentSafetyEnabled ||
         SensitiveDataHandling != SensitiveDataHandling.Disabled);

    /// <summary>
    /// Enforced output checks need the complete answer before any token is released; audit-only checks do not.
    /// </summary>
    public bool RequiresOutputBuffering => Mode == AgentSecurityMode.Enforce &&
        (ContentSafetyEnabled || SensitiveDataHandling != SensitiveDataHandling.Disabled);

    public static EffectiveAgentSecurityPolicy Merge(TenantAiSettings? row, AgentSecurityOptions defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        var mode = ParseOrDefault(row?.AgentSecurityMode, defaults.DefaultMode);
        var sensitiveData = ParseOrDefault(row?.SensitiveDataHandling, defaults.SensitiveDataHandlingByDefault);

        return new(
            mode,
            row?.PromptAttackDetectionEnabled ?? defaults.PromptAttackDetectionEnabledByDefault,
            row?.ContentSafetyEnabled ?? defaults.ContentSafetyEnabledByDefault,
            sensitiveData,
            defaults.FailClosed,
            defaults.HarmSeverityThreshold,
            defaults.MaxInspectionCharacters);
    }

    private static TEnum ParseOrDefault<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}

/// <summary>A metadata-only detector finding. It must never contain the inspected text or matched value.</summary>
public sealed record AgentSecurityFinding(string Detector, string Category);

/// <summary>Result of inspecting one boundary in an agent turn.</summary>
public sealed record AgentSecurityInspection
{
    public required string Text { get; init; }
    public bool Blocked { get; init; }
    public bool Modified { get; init; }
    public bool Unavailable { get; init; }
    public IReadOnlyList<AgentSecurityFinding> Findings { get; init; } = [];

    public bool HasFindings => Findings.Count > 0;
}

/// <summary>Provider-neutral runtime contract used at user, tool, retrieval, and model-output boundaries.</summary>
public interface IAgentSecurityService
{
    public bool HarmfulContentDetectionConfigured { get; }

    public Task<AgentSecurityInspection> InspectAsync(
        string text,
        AgentSecurityStage stage,
        EffectiveAgentSecurityPolicy policy,
        CancellationToken cancellationToken = default);
}

/// <summary>Validates deployment and tenant security-policy values before they can become ambiguous at runtime.</summary>
public static class AgentSecuritySettingsValidator
{
    public static IReadOnlyList<string> ValidateOptions(AgentSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (options.Provider is not ("None" or "AzureContentSafety"))
        {
            errors.Add("AgentSecurity:Provider must be None or AzureContentSafety.");
        }

        if (string.Equals(options.Provider, "AzureContentSafety", StringComparison.Ordinal) &&
            (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("AgentSecurity:Endpoint must be an absolute HTTPS URL when Provider is AzureContentSafety.");
        }

        if (options.ContentSafetyEnabledByDefault && !options.IsAzureContentSafetyConfigured)
        {
            errors.Add("Default content-safety screening requires a configured AzureContentSafety provider.");
        }

        if (options.HarmSeverityThreshold is not (2 or 4 or 6))
        {
            errors.Add("AgentSecurity:HarmSeverityThreshold must be 2 (Low), 4 (Medium), or 6 (High).");
        }

        if (options.MaxInspectionCharacters is < 1 or > 100_000)
        {
            errors.Add("AgentSecurity:MaxInspectionCharacters must be between 1 and 100,000.");
        }

        return errors;
    }

    public static string? ValidateTenantOverrides(
        string? mode,
        string? sensitiveDataHandling,
        bool? contentSafetyEnabled,
        bool externalDetectorsConfigured)
    {
        if (mode is not null && !Enum.TryParse<AgentSecurityMode>(mode, ignoreCase: true, out _))
        {
            return $"agentSecurityMode must be one of: {string.Join(", ", Enum.GetNames<AgentSecurityMode>())}.";
        }

        if (sensitiveDataHandling is not null &&
            !Enum.TryParse<SensitiveDataHandling>(sensitiveDataHandling, ignoreCase: true, out _))
        {
            return $"sensitiveDataHandling must be one of: {string.Join(", ", Enum.GetNames<SensitiveDataHandling>())}.";
        }

        if (contentSafetyEnabled == true && !externalDetectorsConfigured)
        {
            return "Harmful-content screening requires a configured Azure AI Content Safety deployment.";
        }

        return null;
    }
}

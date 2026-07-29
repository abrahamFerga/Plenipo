using Plenipo.Application.Ai;
using Plenipo.Core.Platform;

namespace Plenipo.Application.Tests.Ai;

public sealed class AgentSecuritySettingsTests
{
    [Fact]
    public void TenantOverrides_AreMergedOverDeploymentDefaults()
    {
        var defaults = new AgentSecurityOptions
        {
            DefaultMode = AgentSecurityMode.Audit,
            PromptShieldEnabledByDefault = true,
            ContentSafetyEnabledByDefault = false,
            SensitiveDataHandlingByDefault = SensitiveDataHandling.Redact,
        };
        var tenant = new TenantAiSettings
        {
            AgentSecurityMode = "Enforce",
            PromptShieldEnabled = false,
            ContentSafetyEnabled = true,
            SensitiveDataHandling = "Block",
        };

        var policy = EffectiveAgentSecurityPolicy.Merge(tenant, defaults);

        Assert.Equal(AgentSecurityMode.Enforce, policy.Mode);
        Assert.False(policy.PromptShieldEnabled);
        Assert.True(policy.ContentSafetyEnabled);
        Assert.Equal(SensitiveDataHandling.Block, policy.SensitiveDataHandling);
        Assert.True(policy.RequiresOutputBuffering);
    }

    [Fact]
    public void EnablingExternalControlsWithoutAConfiguredService_IsRejected()
    {
        var error = AgentSecuritySettingsValidator.ValidateTenantOverrides(
            mode: "Enforce",
            sensitiveDataHandling: "Redact",
            promptShieldEnabled: true,
            contentSafetyEnabled: false,
            externalDetectorsConfigured: false);

        Assert.NotNull(error);
        Assert.Contains("Azure AI Content Safety", error);
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData(null, "scramble")]
    public void UnknownEnumOverrides_AreRejected(string? mode, string? sensitive)
    {
        var error = AgentSecuritySettingsValidator.ValidateTenantOverrides(
            mode,
            sensitive,
            promptShieldEnabled: null,
            contentSafetyEnabled: null,
            externalDetectorsConfigured: false);

        Assert.NotNull(error);
    }
}

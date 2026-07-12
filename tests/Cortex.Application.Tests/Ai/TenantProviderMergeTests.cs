using Cortex.Application.Ai;
using Cortex.Core.Platform;
using Xunit;

namespace Cortex.Application.Tests.Ai;

/// <summary>
/// Provider-connection merge semantics: a tenant that sets a provider brings its OWN connection
/// (endpoint + vaulted key, never the deployment's), a bare model override rides the deployment
/// connection, and the validator blocks connections that could not possibly work.
/// </summary>
public sealed class TenantProviderMergeTests
{
    private static AiOptions Defaults() => new()
    {
        Provider = "OpenAI",
        Model = "gpt-4o-mini",
        Endpoint = "https://deployment.example",
        SystemPrompt = "base",
    };

    [Fact]
    public void No_override_inherits_the_deployment_connection()
    {
        var effective = EffectiveAiSettings.Merge((TenantAiSettings?)null, Defaults());

        Assert.Equal("OpenAI", effective.Provider);
        Assert.Equal("gpt-4o-mini", effective.Model);
        Assert.Equal("https://deployment.example", effective.Endpoint);
        Assert.Null(effective.ApiKeySecretRef);
        Assert.False(effective.UsesTenantProvider);
        Assert.True(effective.IsEnabled);
    }

    [Fact]
    public void A_bare_model_override_rides_the_deployment_connection()
    {
        var effective = EffectiveAiSettings.Merge(new TenantAiSettings { Model = "gpt-4.1" }, Defaults());

        Assert.Equal("OpenAI", effective.Provider);
        Assert.Equal("gpt-4.1", effective.Model);
        Assert.Equal("https://deployment.example", effective.Endpoint);
        Assert.False(effective.UsesTenantProvider);
    }

    [Fact]
    public void A_tenant_provider_brings_its_own_connection_and_never_mixes_endpoints_or_keys()
    {
        var effective = EffectiveAiSettings.Merge(new TenantAiSettings
        {
            Provider = "AzureOpenAI",
            Model = "tenant-deployment",
            Endpoint = "https://tenant.openai.azure.com",
            ApiKeySecretRef = "dp:ref",
        }, Defaults());

        Assert.Equal("AzureOpenAI", effective.Provider);
        Assert.Equal("tenant-deployment", effective.Model);
        Assert.Equal("https://tenant.openai.azure.com", effective.Endpoint);
        Assert.Equal("dp:ref", effective.ApiKeySecretRef);
        Assert.True(effective.UsesTenantProvider);
    }

    [Fact]
    public void A_tenant_provider_without_endpoint_or_key_does_not_leak_the_deployments()
    {
        // Even misconfigured, a tenant-owned connection must never fall back to the deployment's
        // endpoint or key — that would silently bill the operator for the tenant's traffic.
        var effective = EffectiveAiSettings.Merge(new TenantAiSettings { Provider = "OpenAI", Model = "gpt-4o" }, Defaults());

        Assert.Null(effective.Endpoint);
        Assert.Null(effective.ApiKeySecretRef);
    }

    [Fact]
    public void Provider_None_disables_chat_for_the_tenant()
    {
        var effective = EffectiveAiSettings.Merge(new TenantAiSettings { Provider = "None" }, Defaults());
        Assert.False(effective.IsEnabled);
    }

    [Theory]
    [InlineData(null, null, null, false, null)] // inherit everything: fine
    [InlineData("Mock", null, null, false, null)] // Mock needs nothing
    [InlineData("OpenAI", "gpt-4o", null, true, null)]
    [InlineData("OpenAI", null, null, true, "model is required")]
    [InlineData("OpenAI", "gpt-4o", null, false, "API key is required")]
    [InlineData("AzureOpenAI", "dep", null, false, "endpoint is required")]
    [InlineData("AzureOpenAI", "dep", "https://x.openai.azure.com", false, null)] // managed identity: key optional
    [InlineData("Anthropic", "claude-sonnet-5", null, true, null)]
    [InlineData("Anthropic", "claude-sonnet-5", null, false, "API key is required")]
    [InlineData("Ollama", "llama3.1", "http://localhost:11434/v1", false, null)]
    [InlineData("Ollama", "llama3.1", null, false, "endpoint is required")]
    [InlineData("Bedrock", "x", null, false, "provider must be one of")]
    [InlineData(null, null, "not a url", false, "absolute http(s) URL")]
    public void Provider_validation_blocks_impossible_connections(
        string? provider, string? model, string? endpoint, bool hasKey, string? expectedFragment)
    {
        var error = TenantAiSettingsValidator.ValidateProvider(provider, model, endpoint, hasKey);
        if (expectedFragment is null)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.NotNull(error);
            Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
        }
    }
}

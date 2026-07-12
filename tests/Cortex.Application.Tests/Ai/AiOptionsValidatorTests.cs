using Cortex.Application.Ai;

namespace Cortex.Application.Tests.Ai;

/// <summary>
/// Covers the fail-fast validation of the "Ai" configuration: the dependency-free providers need nothing,
/// each real provider requires its credentials/endpoint, an unknown provider is rejected, and numeric
/// knobs must be sane. Every default value passes.
/// </summary>
public sealed class AiOptionsValidatorTests
{
    private static AiOptions Options(string provider, Action<AiOptions>? configure = null)
    {
        var options = new AiOptions { Provider = provider };
        configure?.Invoke(options);
        return options;
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Mock")]
    public void DependencyFreeProviders_AreValidWithNoSecrets(string provider)
    {
        Assert.Empty(AiOptionsValidator.Validate(Options(provider)));
    }

    [Fact]
    public void OpenAI_IsTenantConfigured_NotADeploymentDefault()
    {
        Assert.Contains(AiOptionsValidator.Validate(Options("OpenAI")), e => e.Contains("configured per tenant"));
    }

    [Fact]
    public void AzureOpenAI_RequiresAnAbsoluteEndpoint_ButNotAKey()
    {
        // Azure can authenticate with managed identity, so no key is required — only the endpoint.
        Assert.Contains(AiOptionsValidator.Validate(Options("AzureOpenAI")), e => e.Contains("Ai:Endpoint is required"));
        Assert.Contains(
            AiOptionsValidator.Validate(Options("AzureOpenAI", o => o.Endpoint = "not-a-url")),
            e => e.Contains("absolute URL"));
        Assert.Empty(AiOptionsValidator.Validate(Options("AzureOpenAI", o => o.Endpoint = "https://x.openai.azure.com")));
    }

    [Fact]
    public void Ollama_RequiresAnEndpoint()
    {
        Assert.Contains(AiOptionsValidator.Validate(Options("Ollama")), e => e.Contains("Ai:Endpoint is required"));
        Assert.Empty(AiOptionsValidator.Validate(Options("Ollama", o => o.Endpoint = "http://localhost:11434/v1")));
    }

    [Fact]
    public void UnknownProvider_IsRejected_IncludingWrongCase()
    {
        Assert.Contains(AiOptionsValidator.Validate(Options("openai")), e => e.Contains("Unknown Ai:Provider 'openai'"));
    }

    [Fact]
    public void NonSensicalNumbers_AreReported()
    {
        var errors = AiOptionsValidator.Validate(Options("Mock", o =>
        {
            o.MaxToolIterations = 0;
            o.MaxOutputTokens = 0;
            o.MaxConversationTokens = -1;
            o.Temperature = -0.5f;
        }));

        Assert.Contains(errors, e => e.Contains("MaxToolIterations"));
        Assert.Contains(errors, e => e.Contains("MaxOutputTokens"));
        Assert.Contains(errors, e => e.Contains("MaxConversationTokens"));
        Assert.Contains(errors, e => e.Contains("Temperature"));
    }

    [Fact]
    public void ThrowIfInvalid_AggregatesMessages_WhenInvalid()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AiOptionsValidator.ThrowIfInvalid(Options("OpenAI")));
        Assert.Contains("configured per tenant", ex.Message);
    }

    [Fact]
    public void ThrowIfInvalid_DoesNotThrow_ForTheMockDefault()
    {
        AiOptionsValidator.ThrowIfInvalid(Options("Mock"));
    }
}

using Cortex.AspNetCore.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// Unit-level guards on <see cref="AuthSetup"/> (no server or Docker needed — deliberately not in the "api"
/// collection): the X-Dev-* dev-auth fallback is Development-only, and an unconfigured non-Development host
/// fails fast at startup with a clear message instead of registering no handler and 500-ing every request.
/// </summary>
public sealed class AuthSetupTests
{
    private sealed class FakeEnv(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Cortex.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Unconfigured_OutsideDevelopment_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCortexAuthentication(Config(), new FakeEnv("Production")));
        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public void Unconfigured_InDevelopment_RegistersTheDevFallback_WithoutThrowing()
    {
        // No Auth section + Development ⇒ the X-Dev-* dev-auth scheme is registered; must not throw.
        new ServiceCollection().AddCortexAuthentication(Config(), new FakeEnv("Development"));
    }

    [Fact]
    public void Configured_OutsideDevelopment_UsesJwt_WithoutThrowing()
    {
        // Entra External ID configured ⇒ JWT bearer path, valid in any environment.
        new ServiceCollection().AddCortexAuthentication(
            Config(("Auth:Authority", "https://example.ciamlogin.com/tenant/v2.0"), ("Auth:Audience", "api-id")),
            new FakeEnv("Production"));
    }

    [Theory]
    [InlineData("Auth:Authority", "https://example.ciamlogin.com/tenant/v2.0")]
    [InlineData("Auth:Audience", "api-id")]
    public void PartialJwtConfiguration_FailsFast(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCortexAuthentication(
                Config((key, value)), new FakeEnv("Production")));

        Assert.Contains("both Auth:Authority and Auth:Audience", ex.Message, StringComparison.Ordinal);
    }
}

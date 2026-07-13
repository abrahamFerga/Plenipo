using Cortex.Application.Security;
using Microsoft.Extensions.Options;

namespace Cortex.Application.Tests.Security;

public sealed class OutboundUrlPolicyTests
{
    private static OutboundUrlPolicy Policy(bool allowHttp = false, bool allowPrivate = false) =>
        new(Options.Create(new OutboundUrlOptions
        {
            AllowHttp = allowHttp,
            AllowPrivateNetworks = allowPrivate,
        }));

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://10.0.0.10/internal")]
    [InlineData("http://[::1]/admin")]
    public async Task PrivateAndSpecialDestinations_AreRejected(string url)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Policy(allowHttp: true).RequireAllowedAsync(url));
    }

    [Fact]
    public async Task PlainHttp_IsRejectedByDefault()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Policy().RequireAllowedAsync("http://example.com"));
    }

    [Fact]
    public async Task OperatorCanExplicitlyAllowPrivateHttp()
    {
        var uri = await Policy(allowHttp: true, allowPrivate: true)
            .RequireAllowedAsync("http://localhost:11434/v1");

        Assert.Equal("localhost", uri.Host);
    }

    [Fact]
    public void OutboundHandler_DisablesRedirectsAndPinsValidatedDnsConnections()
    {
        using var handler = Policy().CreateHttpMessageHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }
}

using System.Text.Json;
using Plenipo.Application.Agents;
using Plenipo.AspNetCore.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Guards the SignalR chat wire contract (no server/Docker — not in the "api" collection): AddPlenipoRealtime
/// must serialize <see cref="AgentStreamEvent"/> the way the React client (signalr.ts) reads it — its
/// <c>switch (event.type)</c> matches the string names ("Token"/"Usage"/…), NOT the numeric enum values
/// System.Text.Json emits by default, and token fields are camelCase. Drop the JsonStringEnumConverter or the
/// camelCase default in a refactor and the whole chat breaks silently; this fails instantly instead.
/// </summary>
public sealed class RealtimeSetupTests
{
    private static JsonSerializerOptions SignalRPayloadOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlenipoRealtime(new ConfigurationBuilder().Build()); // no Redis connection → in-memory backplane
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;
    }

    [Fact]
    public void SerializesAgentStreamEvents_AsTheReactClientExpects()
    {
        var options = SignalRPayloadOptions();

        var usage = JsonSerializer.Serialize(AgentStreamEvent.UsageReport(10, 20, 30), options);
        Assert.Contains("\"type\":\"Usage\"", usage);        // string name, not 4
        Assert.Contains("\"totalTokens\":30", usage);        // camelCase, matching signalr.ts
        Assert.DoesNotContain("\"Type\":", usage);           // not PascalCase

        var tool = JsonSerializer.Serialize(AgentStreamEvent.ToolInvoked("summarize"), options);
        Assert.Contains("\"type\":\"ToolInvoked\"", tool);
        Assert.Contains("\"toolName\":\"summarize\"", tool);
    }
}

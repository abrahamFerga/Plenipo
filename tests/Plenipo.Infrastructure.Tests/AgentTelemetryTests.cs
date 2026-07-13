using System.Diagnostics;
using Plenipo.Application.Ai;
using Plenipo.Infrastructure.Ai;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// Confirms the runner's instrumentation pattern emits OpenTelemetry activities under the Plenipo.Agents
/// source, so agent runs are observable (e.g. in the Aspire dashboard). Uses an ActivityListener directly
/// — no OpenTelemetry SDK dependency.
/// </summary>
public sealed class AgentTelemetryTests
{
    [Fact]
    public async Task InstrumentedAgent_EmitsActivitiesUnderThePlenipoSource()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        // Build the agent the way the runner does: instrument the chat client and the agent.
        var tracedChatClient = new MockChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: AgentTelemetry.SourceName)
            .Build();
        var agent = tracedChatClient
            .AsBuilder()
            .BuildAIAgent(instructions: "test")
            .AsBuilder()
            .UseOpenTelemetry(sourceName: AgentTelemetry.SourceName)
            .Build();

        await agent.RunAsync("hello");

        Assert.NotEmpty(activities);
    }
}

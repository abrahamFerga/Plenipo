namespace Plenipo.Application.Ai;

/// <summary>
/// OpenTelemetry source name for the agent pipeline. The runner instruments the chat client and the agent
/// under this source (via MAF's <c>UseOpenTelemetry</c>); the tracer registers it (see ServiceDefaults),
/// so agent runs and LLM calls surface in the Aspire dashboard alongside HTTP and DB activity.
/// </summary>
public static class AgentTelemetry
{
    public const string SourceName = "Plenipo.Agents";
}

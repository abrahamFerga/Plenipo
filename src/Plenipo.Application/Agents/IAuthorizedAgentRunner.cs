namespace Plenipo.Application.Agents;

/// <summary>
/// Runs an agent turn under the current user's authority. The security spine of the platform: it
/// resolves the module's tools, filters them to only those the caller is permitted to invoke
/// <em>before</em> building the model request — so the LLM never receives the schema of a tool the
/// user cannot call — wraps each tool for per-invocation auditing, and streams the result back.
/// </summary>
public interface IAuthorizedAgentRunner
{
    public IAsyncEnumerable<AgentStreamEvent> RunAsync(AgentRunRequest request, CancellationToken cancellationToken = default);
}

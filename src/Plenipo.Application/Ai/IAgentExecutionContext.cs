namespace Plenipo.Application.Ai;

/// <summary>
/// What the currently-running agent is allowed to reach, for the scope of one operation. The agent
/// runner populates it once per turn from the resolved profile; services invoked by tools read it.
/// It exists so a tool implementation (retrieval, today) can honour the agent's configured scope
/// without the tool signature — which the model controls — being the thing that carries it.
/// Empty/unset means "no agent-level narrowing", which is what every non-agent caller sees.
/// </summary>
public interface IAgentExecutionContext
{
    /// <summary>
    /// Knowledge-collection patterns the active agent may retrieve from
    /// (see <see cref="AgentCollectionSelection"/>). Null/empty = no narrowing.
    /// </summary>
    public IReadOnlyList<string>? CollectionScopes { get; }
}

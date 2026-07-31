using Plenipo.Application.Ai;

namespace Plenipo.Infrastructure.Context;

/// <summary>
/// Scoped, mutable backing for <see cref="IAgentExecutionContext"/> — same shape as
/// <see cref="RequestContext"/>: consumers depend on the read-only interface, and exactly one
/// component (the agent runner) populates it.
/// </summary>
public sealed class AgentExecutionContext : IAgentExecutionContext
{
    public IReadOnlyList<string>? CollectionScopes { get; private set; }

    /// <summary>Called by the agent runner once the profile for the turn is resolved.</summary>
    public void SetCollectionScopes(IReadOnlyList<string>? scopes) =>
        CollectionScopes = scopes is { Count: > 0 } ? scopes : null;
}

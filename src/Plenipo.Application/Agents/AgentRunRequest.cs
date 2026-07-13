namespace Plenipo.Application.Agents;

/// <summary>A single user turn against a module's agent.</summary>
public sealed record AgentRunRequest
{
    /// <summary>The module whose tools and instructions scope this conversation.</summary>
    public required string ModuleId { get; init; }

    /// <summary>Existing conversation to continue, or <c>null</c> to start a new one.</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>The user's message.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// The agent to run this turn: a tenant profile's or manifest agent's name. Null = the default
    /// (the tenant's default profile, else the module's default manifest agent, else the plain
    /// module assistant). An unknown name fails the turn readably.
    /// </summary>
    public string? Agent { get; init; }

    /// <summary>
    /// Model override for this turn, within the tenant's provider connection. Must be one of the
    /// deployment's advertised <c>Ai:AvailableModels</c> (or the default model). Null = the agent's
    /// pinned model, else the tenant/deployment default.
    /// </summary>
    public string? Model { get; init; }
}

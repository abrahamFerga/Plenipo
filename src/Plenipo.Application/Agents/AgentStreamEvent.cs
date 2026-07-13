namespace Plenipo.Application.Agents;

public enum AgentStreamEventType
{
    /// <summary>A chunk of assistant text.</summary>
    Token = 0,

    /// <summary>A tool was invoked (surfaced to the UI for transparency).</summary>
    ToolInvoked = 1,

    /// <summary>The turn completed successfully.</summary>
    Completed = 2,

    /// <summary>The turn failed.</summary>
    Error = 3,

    /// <summary>Token usage for the turn (surfaced to the UI for cost visibility).</summary>
    Usage = 4,

    /// <summary>A side-effecting tool was blocked pending human approval (it did not execute).</summary>
    ApprovalRequired = 5,
}

/// <summary>One streamed event from an agent turn, delivered over SignalR or SSE.</summary>
public sealed record AgentStreamEvent
{
    public required AgentStreamEventType Type { get; init; }

    /// <summary>Assistant text for <see cref="AgentStreamEventType.Token"/>.</summary>
    public string? Text { get; init; }

    /// <summary>Tool name for <see cref="AgentStreamEventType.ToolInvoked"/>.</summary>
    public string? ToolName { get; init; }

    /// <summary>Conversation id, set on the first event so the client can persist it.</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>Error message for <see cref="AgentStreamEventType.Error"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Prompt token count for <see cref="AgentStreamEventType.Usage"/>.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Completion token count for <see cref="AgentStreamEventType.Usage"/>.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Total token count for <see cref="AgentStreamEventType.Usage"/>.</summary>
    public long? TotalTokens { get; init; }

    public static AgentStreamEvent Token(string text) => new() { Type = AgentStreamEventType.Token, Text = text };
    public static AgentStreamEvent ToolInvoked(string toolName) => new() { Type = AgentStreamEventType.ToolInvoked, ToolName = toolName };
    public static AgentStreamEvent NeedsApproval(string toolName) => new() { Type = AgentStreamEventType.ApprovalRequired, ToolName = toolName };
    public static AgentStreamEvent Completed(Guid conversationId) => new() { Type = AgentStreamEventType.Completed, ConversationId = conversationId };
    public static AgentStreamEvent Failed(string error) => new() { Type = AgentStreamEventType.Error, Error = error };
    public static AgentStreamEvent UsageReport(long input, long output, long total) =>
        new() { Type = AgentStreamEventType.Usage, InputTokens = input, OutputTokens = output, TotalTokens = total };
}

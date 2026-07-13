namespace Plenipo.Application.Conversations;

/// <summary>
/// Derives a conversation's display title from its first user message: trimmed and capped at 60 characters
/// (a longer message is truncated with an ellipsis). An empty or whitespace-only message falls back to a
/// friendly default so the conversation list never shows a blank entry (a non-browser client — the .http
/// catalog, AG-UI — can send one; the browser gates it).
/// </summary>
public static class ConversationTitle
{
    public const int MaxLength = 60;
    public const string Fallback = "New conversation";

    public static string Derive(string? firstMessage)
    {
        var trimmed = firstMessage?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Fallback;
        }

        return trimmed.Length <= MaxLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, MaxLength - 3), "...");
    }
}

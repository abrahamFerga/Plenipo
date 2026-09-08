using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Plenipo.Testing.AgUi;

/// <summary>
/// What actually happened during one AG-UI turn, parsed from the server-sent event stream: the
/// event types in order, the tools that started, the custom events (<c>approval_required</c>,
/// <c>token_usage</c>), and the assistant's streamed text. This is the one parser every product
/// used to copy; a change to the protocol now reaches every suite through the package.
/// </summary>
public sealed record AgUiRun(
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<string> ToolCalls,
    IReadOnlyList<string> CustomEvents,
    string AssistantText,
    string RawSse)
{
    /// <summary>The turn ran to <c>RUN_FINISHED</c> without a <c>RUN_ERROR</c>.</summary>
    public bool Completed =>
        EventTypes.Contains("RUN_STARTED") && EventTypes.Contains("RUN_FINISHED") && !EventTypes.Contains("RUN_ERROR");

    /// <summary>The approval gate fired for a side-effecting tool.</summary>
    public bool RequiredApproval => CustomEvents.Contains("approval_required");

    /// <summary>The runner reported token usage for the turn.</summary>
    public bool ReportedUsage => CustomEvents.Contains("token_usage");

    /// <summary>Position of the first event of the given type, or -1.</summary>
    public int IndexOf(string eventType)
    {
        for (var i = 0; i < EventTypes.Count; i++)
        {
            if (string.Equals(EventTypes[i], eventType, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Parses the raw <c>text/event-stream</c> body of an AG-UI response.</summary>
    public static AgUiRun Parse(string sse)
    {
        ArgumentNullException.ThrowIfNull(sse);

        var types = new List<string>();
        var tools = new List<string>();
        var customs = new List<string>();
        var text = new StringBuilder();

        foreach (var line in sse.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line["data: ".Length..]);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString() ?? "";
            types.Add(type);

            switch (type)
            {
                case "TOOL_CALL_START" when root.TryGetProperty("toolCallName", out var name):
                    tools.Add(name.GetString() ?? "");
                    break;
                case "TEXT_MESSAGE_CONTENT" when root.TryGetProperty("delta", out var delta):
                    text.Append(delta.GetString());
                    break;
                case "CUSTOM" when root.TryGetProperty("name", out var custom):
                    customs.Add(custom.GetString() ?? "");
                    break;
                default:
                    break;
            }
        }

        return new AgUiRun(types, tools, customs, text.ToString(), sse);
    }
}

/// <summary>One-call AG-UI turns for tests: post a user message to a module's agent and parse the stream.</summary>
public static class AgUiClient
{
    /// <summary>
    /// Sends one user turn to <c>/api/agui/{moduleId}</c> and returns the parsed run. The response
    /// must be successful; a 403 here means the caller's role may not chat, which is a test-setup
    /// error rather than a finding.
    /// </summary>
    public static async Task<AgUiRun> ChatAsync(
        this HttpClient client, string moduleId, string message, string? threadId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var body = new
        {
            threadId,
            messages = new[] { new { id = "m1", role = "user", content = message } },
        };

        using var response = await client.PostAsJsonAsync($"/api/agui/{moduleId}", body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Quote the endpoint's body: a bare status hid the tool's own refusal from the product
            // that hit it (#216), and the kit's guidance about WritePrompt lives in that detail.
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"POST /api/agui/{moduleId} returned {(int)response.StatusCode} {response.StatusCode}: {detail}",
                inner: null,
                response.StatusCode);
        }

        return AgUiRun.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}

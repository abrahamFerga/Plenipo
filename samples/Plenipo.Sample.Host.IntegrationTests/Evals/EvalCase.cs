using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plenipo.Sample.Host.IntegrationTests.Evals;

/// <summary>
/// One golden-conversation eval: a user turn against a module's agent and the behavioral
/// contract the platform must honor for it — which tools get called, whether the approval gate
/// fires, what the reply must (not) say. Cases are data (JSON under Evals/cases), so changing an
/// agent profile, manifest instruction, or tool description gets regression coverage without
/// writing a test. Runs on the Mock provider: deterministic, keyless, CI-safe.
/// </summary>
public sealed record EvalCase
{
    public required string Name { get; init; }
    public required string Module { get; init; }
    public required string Message { get; init; }

    /// <summary>Role for the dev-auth client (defaults to the all-permissions operator).</summary>
    public string Role { get; init; } = "system_admin";

    /// <summary>Tool names that must appear as TOOL_CALL_START events, in any order.</summary>
    public string[] ExpectToolCalls { get; init; } = [];

    /// <summary>Tool names that must NOT be invoked (e.g. proving RBAC or approval blocking).</summary>
    public string[] ForbidToolCalls { get; init; } = [];

    /// <summary>Whether the human-approval gate must fire (CUSTOM approval_required event).</summary>
    public bool ExpectApproval { get; init; }

    /// <summary>Case-insensitive substrings the assistant's streamed text must contain.</summary>
    public string[] ReplyMustContain { get; init; } = [];

    /// <summary>Case-insensitive substrings the assistant's streamed text must NOT contain.</summary>
    public string[] ReplyMustNotContain { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, // a typo'd field fails loudly
    };

    public static EvalCase Load(string path) =>
        JsonSerializer.Deserialize<EvalCase>(File.ReadAllText(path), Json)
        ?? throw new InvalidOperationException($"Empty eval case: {path}");
}

/// <summary>What actually happened during the turn, parsed from the AG-UI SSE stream.</summary>
public sealed record EvalRun(
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<string> ToolCalls,
    IReadOnlyList<string> CustomEvents,
    string AssistantText,
    string RawSse)
{
    public static EvalRun Parse(string sse)
    {
        var types = new List<string>();
        var tools = new List<string>();
        var customs = new List<string>();
        var text = new System.Text.StringBuilder();

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

        return new EvalRun(types, tools, customs, text.ToString(), sse);
    }
}

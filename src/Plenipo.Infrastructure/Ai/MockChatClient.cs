using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Plenipo.Infrastructure.Ai;

/// <summary>
/// A deterministic, dependency-free <see cref="IChatClient"/> for development and demos. It streams a
/// contextual canned reply and reports estimated token usage — so the entire chat pipeline works end to
/// end without an external AI provider or API key: streaming, conversation persistence, token-usage
/// tracking, the AG-UI protocol, and SignalR all function.
/// <para>
/// Crucially, it also performs <em>real tool calls</em>: when the user asks it to use a tool (or names
/// one), it emits a function call for an available tool, which Plenipo dispatches through the genuine
/// pipeline — permission-filtered before the model sees it, audited, and (for side-effecting tools)
/// routed to human approval. This lets the zero-config demo showcase Plenipo's signature capability
/// without an API key. Configure a real provider (OpenAI / AzureOpenAI / Ollama) for genuine reasoning.
/// </para>
/// </summary>
public sealed class MockChatClient : IChatClient
{
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        // First turn: if the user is asking for a tool, emit a function call. Plenipo's function-invocation
        // pipeline runs it (permission gate already applied to options.Tools, plus audit + HITL), then calls
        // us again with the result in history — which the summary branch below handles.
        if (!HasToolActivity(list)
            && SelectTool(LastUserText(list), options) is { } tool)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                Contents = [new FunctionCallContent(CallId(tool), tool.Name, SynthesizeArguments(tool, LastUserText(list)))],
            };
            yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
            {
                Contents = [new UsageContent(EstimateUsage(list, tool.Name))],
            };
            yield break;
        }

        var reply = BuildReply(list, options);

        // Stream the reply word by word so the UI shows a natural typing effect.
        foreach (var chunk in Tokenize(reply))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        // Final update carries the usage so the runner records token consumption.
        yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
        {
            Contents = [new UsageContent(EstimateUsage(list, reply))],
        };
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        if (!HasToolActivity(list)
            && SelectTool(LastUserText(list), options) is { } tool)
        {
            var call = new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(CallId(tool), tool.Name, SynthesizeArguments(tool, LastUserText(list)))]);
            return Task.FromResult(new ChatResponse(call)
            {
                ModelId = "plenipo-mock",
                Usage = EstimateUsage(list, tool.Name),
            });
        }

        var reply = BuildReply(list, options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
        {
            ModelId = "plenipo-mock",
            Usage = EstimateUsage(list, reply),
        });
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // Nothing to dispose.
    }

    // ── Reply text ───────────────────────────────────────────────────────────

    private static string BuildReply(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        // If a tool ran this turn, summarize its result rather than emitting a canned greeting.
        var toolResult = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .LastOrDefault();
        if (toolResult is not null)
        {
            return BuildToolSummary(messages, toolResult);
        }

        var lastUser = LastUserText(messages);
        var toolNames = options?.Tools?.OfType<AIFunction>().Select(t => t.Name).ToArray() ?? [];

        var sb = new StringBuilder();
        sb.Append("Hi! I'm the Plenipo assistant running in mock mode — no AI provider is configured, ");
        sb.Append("so this is a canned response that proves the chat pipeline works end to end. ");

        if (!string.IsNullOrWhiteSpace(lastUser))
        {
            sb.Append("You said: \"").Append(Trim(lastUser, 200)).Append("\". ");
        }

        if (toolNames.Length > 0)
        {
            sb.Append("Ask me to use a tool — say \"use a tool\", or name one — and I'll actually call it ");
            sb.Append("(Plenipo routes it through the same permission-checked, audited pipeline a real model would). ");
            sb.Append("Tools you're authorized to use here: ").Append(string.Join(", ", toolNames)).Append(". ");
        }
        else
        {
            sb.Append("You currently have no tools available in this module. ");
        }

        sb.Append("Set Ai:Provider to OpenAI, AzureOpenAI, or Ollama (with a key/endpoint) to enable real answers.");
        return sb.ToString();
    }

    private static string BuildToolSummary(IReadOnlyList<ChatMessage> messages, FunctionResultContent result)
    {
        var name = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(c => c.CallId == result.CallId)?.Name ?? "a tool";
        var resultText = Trim(result.Result?.ToString() ?? "(no output)", 400);

        var sb = new StringBuilder();
        sb.Append("Done — I called the ").Append(name).Append(" tool. ");
        sb.Append("Plenipo ran it through the security pipeline (permission-checked before the model saw it, ");
        sb.Append("then audited), so it now appears under Admin → Audit. It returned: ");
        sb.Append(resultText).Append(". ");
        sb.Append("(Mock mode — configure a real AI provider for genuine reasoning over these tools.)");
        return sb.ToString();
    }

    // ── Tool selection + argument synthesis ──────────────────────────────────

    /// <summary>True once any function call/result is in the history — we only initiate a tool call on the
    /// first turn, never in response to a tool result (which would loop).</summary>
    private static bool HasToolActivity(IReadOnlyList<ChatMessage> messages)
    {
        // Only THIS turn's activity matters: a function call/result after the last user message
        // means the pipeline already ran the tool and now wants the summary. Tool calls from
        // EARLIER turns must not block new ones — a multi-turn conversation (or a workflow step
        // resuming the same conversation) asks again.
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User)
            {
                return false;
            }

            if (messages[i].Contents.Any(c => c is FunctionCallContent or FunctionResultContent))
            {
                return true;
            }
        }

        return false;
    }

    private static string? LastUserText(IReadOnlyList<ChatMessage> messages) =>
        messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text?.Trim();

    private static string CallId(AIFunction tool) => "mock-" + tool.Name;

    /// <summary>
    /// Picks a tool to call from the (already permission-filtered) <paramref name="options"/> tools based on
    /// the user's message: a word shared with a tool's name wins; otherwise the generic word "tool" calls the
    /// simplest available tool. Returns null when the user isn't asking for a tool, so ordinary chat stays text.
    /// </summary>
    private static AIFunction? SelectTool(string? userText, ChatOptions? options)
    {
        var tools = options?.Tools?.OfType<AIFunction>().ToArray() ?? [];
        if (tools.Length == 0 || string.IsNullOrWhiteSpace(userText))
        {
            return null;
        }

        var text = userText.ToLowerInvariant();

        // 1) Name-token match — "summarize my spending" → summarize_spending.
        AIFunction? best = null;
        var bestScore = 0;
        foreach (var tool in tools)
        {
            var score = NameTokens(tool.Name).Count(token => text.Contains(token, StringComparison.Ordinal));
            if (score > bestScore)
            {
                bestScore = score;
                best = tool;
            }
        }

        if (best is not null)
        {
            return best;
        }

        // 2) Generic ask — "use a tool" → the simplest tool (fewest required args = cleanest demo).
        if (text.Contains("tool", StringComparison.Ordinal))
        {
            return tools.OrderBy(RequiredCount).ThenBy(t => t.Name, StringComparer.Ordinal).First();
        }

        return null;
    }

    /// <summary>Significant lowercase words in a tool name (snake/kebab/space-separated, length ≥ 4).</summary>
    private static IEnumerable<string> NameTokens(string name) =>
        name.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.ToLowerInvariant())
            .Where(part => part.Length >= 4);

    private static int RequiredCount(AIFunction tool)
    {
        try
        {
            var schema = tool.JsonSchema;
            if (schema.ValueKind == JsonValueKind.Object
                && schema.TryGetProperty("required", out var required)
                && required.ValueKind == JsonValueKind.Array)
            {
                return required.GetArrayLength();
            }
        }
        catch (InvalidOperationException)
        {
            // Schema unavailable — treat as zero required.
        }

        return 0;
    }

    /// <summary>
    /// Builds a minimal valid argument set from the tool's JSON schema. Every required property gets a value;
    /// the FIRST required string parameter is filled with the user's message (their intent) so search/lookup
    /// tools get a real term instead of a placeholder — the rest get type-appropriate placeholders. Generic
    /// (no module knowledge) so it works for any module's tools.
    /// </summary>
    private static Dictionary<string, object?> SynthesizeArguments(AIFunction tool, string? userText)
    {
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);

        JsonElement schema;
        try
        {
            schema = tool.JsonSchema;
        }
        catch (InvalidOperationException)
        {
            return args;
        }

        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return args;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredList) && requiredList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredList.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    required.Add(item.GetString()!);
                }
            }
        }

        var messageUsed = false;
        // Quoted spans map to string params IN PARAMETER ORDER — the deterministic demo convention:
        // "Log 0.5h on 'Vandelay acquisition' …" puts the quoted name into the first string param
        // (matterName), a second quoted span into the second, and so on. Makes multi-argument tools
        // (create_matter, add_deadline, log_time) actually usable in the keyless demo.
        var quoted = ExtractQuotedSpans(userText);

        foreach (var property in properties.EnumerateObject())
        {
            // Only supply REQUIRED arguments; optional parameters keep the tool's own defaults. Filling
            // optionals with type placeholders is wrong — e.g. an int `days` → 1 would make summarize_spending
            // look back a single day and miss the demo ledger, and a `category` → "example" would match nothing.
            if (!required.Contains(property.Name))
            {
                continue;
            }

            var type = ReadType(property.Value);

            // Date-named string params take the first ISO date in the message ("due 2026-08-14"),
            // never a quoted span — those are for names/titles.
            if (type == "string"
                && property.Name.Contains("date", StringComparison.OrdinalIgnoreCase)
                && TryFirstIsoDate(userText, out var isoDate))
            {
                args[property.Name] = isoDate;
            }
            // Quoted spans fill string params in order.
            else if (type == "string" && quoted.Count > 0)
            {
                args[property.Name] = quoted.Dequeue();
            }
            // Otherwise the first remaining required string gets the whole message, so search/draft
            // tools get a real term.
            else if (!messageUsed && !string.IsNullOrWhiteSpace(userText) && type == "string")
            {
                args[property.Name] = userText.Trim();
                messageUsed = true;
            }
            // A required number takes the first number in the message ("record a 250 expense" → 250) so a
            // recorded amount or portion reflects what the user asked for, not a placeholder.
            else if ((type == "number" || type == "integer") && TryFirstNumber(userText, out var number))
            {
                args[property.Name] = type == "integer" ? (object)(long)number : number;
            }
            else
            {
                args[property.Name] = DefaultForSchema(property.Value);
            }
        }

        return args;
    }

    /// <summary>
    /// Quoted spans ('…' or "…") in order of appearance — the demo's explicit-argument syntax.
    /// Boundary lookarounds + a matching-quote backreference keep contractions ("the firm's") and
    /// mixed quotes from producing phantom spans.
    /// </summary>
    private static Queue<string> ExtractQuotedSpans(string? text)
    {
        var spans = new Queue<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return spans;
        }

        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     text, @"(?<=^|[\s(])(['""])(?<v>[^'""]{2,200}?)\1(?=[\s).,;:!?]|$)"))
        {
            spans.Enqueue(match.Groups["v"].Value.Trim());
        }

        return spans;
    }

    /// <summary>The first ISO date (yyyy-MM-dd) in the text, for date-named string params.</summary>
    private static bool TryFirstIsoDate(string? text, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = System.Text.RegularExpressions.Regex.Match(text, @"\b(\d{4}-\d{2}-\d{2})\b");
        if (!match.Success)
        {
            return false;
        }

        value = match.Groups[1].Value;
        return true;
    }

    /// <summary>Parses the first number in the text ("record a 250 expense" → 250) for a numeric tool arg.</summary>
    private static bool TryFirstNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var i = 0;
        while (i < text.Length && !char.IsDigit(text[i]))
        {
            i++;
        }
        if (i == text.Length)
        {
            return false;
        }

        var start = i;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
        {
            i++;
        }

        return double.TryParse(
            text.AsSpan(start, i - start).TrimEnd('.'),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static object? DefaultForSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return "example";
        }

        if (schema.TryGetProperty("enum", out var enumValues)
            && enumValues.ValueKind == JsonValueKind.Array
            && enumValues.GetArrayLength() > 0)
        {
            var first = enumValues[0];
            return first.ValueKind == JsonValueKind.String ? first.GetString() : first.ToString();
        }

        return ReadType(schema) switch
        {
            "string" => "example",
            "integer" => 1,
            "number" => 1,
            "boolean" => true,
            "array" => Array.Empty<object?>(),
            "object" => new Dictionary<string, object?>(),
            _ => "example",
        };
    }

    private static string? ReadType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type))
        {
            return null;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString();
        }

        // Nullable types are expressed as ["string", "null"] — take the first non-null entry.
        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in type.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name && name != "null")
                {
                    return name;
                }
            }
        }

        return null;
    }

    // ── Streaming + usage helpers ────────────────────────────────────────────

    /// <summary>Splits text into word-plus-trailing-space chunks for streaming.</summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    /// <summary>Rough token estimate (~4 chars/token), enough to exercise usage tracking and dashboards.</summary>
    private static UsageDetails EstimateUsage(IReadOnlyList<ChatMessage> messages, string reply)
    {
        var inputChars = messages.Sum(m => m.Text?.Length ?? 0);
        long input = Math.Max(1, inputChars / 4);
        long output = Math.Max(1, reply.Length / 4);

        return new UsageDetails
        {
            InputTokenCount = input,
            OutputTokenCount = output,
            TotalTokenCount = input + output,
        };
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Plenipo.Testing.Evals;

/// <summary>
/// One golden-conversation eval: a user turn against a module's agent and the behavioural contract
/// the platform must honour for it — which tools get called, whether the approval gate fires, what
/// the reply must (not) say. Cases are data (JSON under <c>Evals/cases</c>), so changing an agent
/// profile, manifest instruction, or tool description gets regression coverage without writing a
/// test. Runs on the Mock provider: deterministic, keyless, CI-safe.
/// </summary>
public sealed record EvalCase
{
    public required string Name { get; init; }
    public required string Module { get; init; }
    public required string Message { get; init; }

    /// <summary>Role for the dev-auth client (defaults to the all-permissions operator).</summary>
    public string Role { get; init; } = "system_admin";

    /// <summary>Tool names that must appear as <c>TOOL_CALL_START</c> events, in any order.</summary>
    public string[] ExpectToolCalls { get; init; } = [];

    /// <summary>Tool names that must NOT be invoked (e.g. proving RBAC or approval blocking).</summary>
    public string[] ForbidToolCalls { get; init; } = [];

    /// <summary>Whether the human-approval gate must fire (<c>CUSTOM approval_required</c> event).</summary>
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

/// <summary>Discovers the eval cases copied next to the test assembly (<c>Evals/cases/*.json</c>).</summary>
public static class EvalCases
{
    /// <summary>Where the consuming test project's cases land at run time.</summary>
    public static string CasesDirectory => Path.Combine(AppContext.BaseDirectory, "Evals", "cases");

    /// <summary>Case names, sorted.</summary>
    public static string[] Names() =>
        Directory.Exists(CasesDirectory)
            ? Directory.EnumerateFiles(CasesDirectory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null)
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray()
            : [];

    /// <summary>Case names for <c>[MemberData]</c>.</summary>
    public static TheoryData<string> All()
    {
        var data = new TheoryData<string>();
        foreach (var name in Names())
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>Loads one case by name.</summary>
    public static EvalCase Load(string caseName) => EvalCase.Load(Path.Combine(CasesDirectory, $"{caseName}.json"));
}

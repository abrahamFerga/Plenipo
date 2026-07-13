using System.Text.Json;
using System.Text.Json.Nodes;

namespace Plenipo.Cli;

/// <summary>
/// The wizard's decisions, decoupled from any console so the merge logic is a pure, testable
/// function. Every field is optional: null means "leave whatever the file already says" — which is
/// what makes re-running `plenipo init` non-destructive by default (the OpenClaw property).
/// </summary>
public sealed record SettingsPlan
{
    public string? AiProvider { get; init; }
    public string? AiModel { get; init; }
    public string? AiEndpoint { get; init; }

    public bool? RagEnabled { get; init; }
    public string? EmbeddingProvider { get; init; }
    public string? EmbeddingModel { get; init; }

    public bool? DocumentsEnabled { get; init; }

    public bool? WhatsAppEnabled { get; init; }

    public string? FilesProvider { get; init; }

    public string? AuthAuthority { get; init; }
    public string? AuthAudience { get; init; }
    public string? PermissionSource { get; init; }

    public bool? SkillsEnabled { get; init; }
    public string? SkillsPath { get; init; }

    public string? SecretsProvider { get; init; }
    public string? KeyVaultUri { get; init; }
}

/// <summary>
/// Merges a <see cref="SettingsPlan"/> into an existing plenipo.settings.json (or an empty one):
/// only decided keys are touched, everything else — including sections the wizard knows nothing
/// about — survives verbatim. Secrets never pass through here by design.
/// </summary>
public static class PlenipoSettingsFile
{
    public const string FileName = "plenipo.settings.json";

    public static string Merge(string? existingJson, SettingsPlan plan)
    {
        var root = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonObject()
            : JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();

        Set(root, plan.AiProvider, "Ai", "Provider");
        Set(root, plan.AiModel, "Ai", "Model");
        Set(root, plan.AiEndpoint, "Ai", "Endpoint");

        Set(root, plan.RagEnabled, "Rag", "Enabled");
        Set(root, plan.EmbeddingProvider, "Rag", "EmbeddingProvider");
        Set(root, plan.EmbeddingModel, "Rag", "EmbeddingModel");

        Set(root, plan.DocumentsEnabled, "Documents", "Enabled");

        Set(root, plan.WhatsAppEnabled, "Channels", "WhatsApp", "Enabled");

        Set(root, plan.FilesProvider, "Files", "Provider");

        Set(root, plan.AuthAuthority, "Auth", "Authority");
        Set(root, plan.AuthAudience, "Auth", "Audience");
        Set(root, plan.PermissionSource, "Auth", "PermissionSource");

        Set(root, plan.SkillsEnabled, "Skills", "Enabled");
        Set(root, plan.SkillsPath, "Skills", "Path");

        Set(root, plan.SecretsProvider, "Secrets", "Provider");
        Set(root, plan.KeyVaultUri, "Secrets", "KeyVaultUri");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            root.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Set(JsonObject root, object? value, params string[] path)
    {
        if (value is null)
        {
            return;
        }

        var current = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (current[path[i]] is not JsonObject next)
            {
                next = new JsonObject();
                current[path[i]] = next;
            }

            current = next;
        }

        current[path[^1]] = JsonValue.Create(value);
    }
}

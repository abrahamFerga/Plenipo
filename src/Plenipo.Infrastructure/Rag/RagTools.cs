using System.ComponentModel;
using System.Text;
using Plenipo.Application.Agents;
using Plenipo.Application.Authorization;
using Plenipo.Application.Rag;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// The retrieval tools every module's agent gets when RAG is enabled. Results come back as quoted
/// passages with file citations, mirroring the excerpt-with-citation contract the document tools
/// established — the agent is instructed to cite, and the tool output makes that the path of least
/// resistance. Both tools see only what the caller, the collection gates, the chunk ACLs and the
/// agent's own collection scope allow; nothing here can widen access.
/// </summary>
public sealed class RagTools(IRagService rag)
{
    [Description("Search the indexed knowledge collections (ingested documents) for relevant passages. Returns quoted excerpts with the source file name, file id, and page when the source is paginated — cite them for every claim. Use this to answer questions across many documents; use read_document to read one specific file in full.")]
    public async Task<string> SearchKnowledge(
        [Description("What to look for — a question or key phrases.")] string query,
        [Description("Optional collection name to search within (e.g. 'matter: Acme diligence'). Omit to search every collection you can access.")]
        string? collection = null,
        [Description("Optional facet filter as key=value pairs separated by semicolons (e.g. 'jurisdiction=ES;lawArea=employment'). Call list_knowledge_collections first to see which keys exist. Passages that do not carry every pair are excluded.")]
        string? filters = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = ParseFilters(filters);
        var hits = await rag.SearchAsync(query, collection, topK: null, filters: parsed, cancellationToken: cancellationToken);
        if (hits.Count == 0)
        {
            return Describe(collection, parsed);
        }

        var sb = new StringBuilder($"Top {hits.Count} passage(s):\n");
        foreach (var hit in hits)
        {
            // The page goes immediately after the file, where a reader expects a citation to carry
            // it. Omitted entirely when unknown — a missing page is better than a wrong one.
            var page = hit.PageCitation is { } citation ? $", {citation}" : string.Empty;
            sb.AppendLine();
            sb.AppendLine($"\"{hit.Text}\"");
            sb.AppendLine($"— source: {hit.FileName}{page} (file id: {hit.FileId}), chunk {hit.Ordinal + 1}, collection: {hit.CollectionName}");
        }

        return sb.ToString();
    }

    [Description("List the knowledge collections you can search, with how many documents each holds and which facet keys can be used as filters. Call this when you do not know what knowledge is available, or before using a filter.")]
    public async Task<string> ListKnowledgeCollections(CancellationToken cancellationToken = default)
    {
        var collections = await rag.ListCollectionsAsync(cancellationToken);
        if (collections.Count == 0)
        {
            return "No knowledge collections are available to you. Documents may not be indexed yet.";
        }

        var sb = new StringBuilder($"{collections.Count} collection(s) available:\n");
        foreach (var c in collections)
        {
            sb.AppendLine();
            sb.AppendLine($"- \"{c.Name}\" — {c.DocumentCount} document(s), {c.ChunkCount} passage(s), language: {c.Language}");
            if (c.FilterKeys.Count > 0)
            {
                sb.AppendLine($"  filter keys: {string.Join(", ", c.FilterKeys)}");
            }

            if (c.Metadata.Count > 0)
            {
                sb.AppendLine($"  about: {string.Join(", ", c.Metadata.Select(kv => $"{kv.Key}={kv.Value}"))}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// "key=value;key=value" — a flat string rather than a nested object because every provider's
    /// function-calling schema handles a string reliably, and the model composes this form well.
    /// Malformed segments are skipped rather than rejected: a bad filter should narrow nothing, not
    /// fail the turn.
    /// </summary>
    internal static Dictionary<string, string>? ParseFilters(string? filters)
    {
        if (string.IsNullOrWhiteSpace(filters))
        {
            return null;
        }

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in filters.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var split = segment.IndexOf('=');
            if (split <= 0 || split == segment.Length - 1)
            {
                continue;
            }

            var key = segment[..split].Trim();
            var value = segment[(split + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0)
            {
                parsed[key] = value;
            }
        }

        return parsed.Count > 0 ? parsed : null;
    }

    private static string Describe(string? collection, Dictionary<string, string>? filters)
    {
        var sb = new StringBuilder("No indexed passages matched");
        if (collection is not null)
        {
            sb.Append($" in collection '{collection}'");
        }

        if (filters is not null)
        {
            sb.Append($" with filter {string.Join(";", filters.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }

        sb.Append('.');
        return collection is null && filters is null
            ? sb.Append(" Documents may not be indexed yet — call list_knowledge_collections to see what is available.").ToString()
            : sb.Append(" Call list_knowledge_collections to check the collection name and available filter keys, or retry without the narrowing.").ToString();
    }
}

/// <summary>
/// Exposes the knowledge tools to every module's agent under the <c>knowledge</c> pseudo-module
/// (permissions <c>tools.knowledge.search_knowledge</c> and
/// <c>tools.knowledge.list_knowledge_collections</c>). Registered only when <c>Rag:Enabled</c> —
/// the model never sees a tool this deployment cannot execute.
/// </summary>
public sealed class RagToolSource : IPlatformToolSource
{
    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<RagTools>();
        return
        [
            new ModuleTool
            {
                ModuleId = Permissions.KnowledgeToolModule,
                Name = "search_knowledge",
                Permission = Permissions.ForTool(Permissions.KnowledgeToolModule, "search_knowledge"),
                Function = AIFunctionFactory.Create(tools.SearchKnowledge, name: "search_knowledge"),
            },
            new ModuleTool
            {
                ModuleId = Permissions.KnowledgeToolModule,
                Name = "list_knowledge_collections",
                Permission = Permissions.ForTool(Permissions.KnowledgeToolModule, "list_knowledge_collections"),
                Function = AIFunctionFactory.Create(tools.ListKnowledgeCollections, name: "list_knowledge_collections"),
            },
        ];
    }
}

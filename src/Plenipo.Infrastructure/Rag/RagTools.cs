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
/// The retrieval tool every module's agent gets when RAG is enabled. Results come back as quoted
/// passages with file citations, mirroring the excerpt-with-citation contract the document tools
/// established — the agent is instructed to cite, and the tool output makes that the path of least
/// resistance.
/// </summary>
public sealed class RagTools(IRagService rag)
{
    [Description("Search the indexed knowledge collections (ingested documents) for relevant passages. Returns quoted excerpts with the source file name and file id — cite them for every claim. Use this to answer questions across many documents; use read_document to read one specific file in full.")]
    public async Task<string> SearchKnowledge(
        [Description("What to look for — a question or key phrases.")] string query,
        [Description("Optional collection name to search within (e.g. 'matter: Acme diligence'). Omit to search every collection you can access.")]
        string? collection = null,
        CancellationToken cancellationToken = default)
    {
        var hits = await rag.SearchAsync(query, collection, cancellationToken: cancellationToken);
        if (hits.Count == 0)
        {
            return collection is null
                ? "No indexed passages matched. Documents may not be indexed yet — indexing tools (e.g. index_matter_documents) build the collection first."
                : $"No indexed passages matched in collection '{collection}' (it may not exist, be empty, or be restricted).";
        }

        var sb = new StringBuilder($"Top {hits.Count} passage(s):\n");
        foreach (var hit in hits)
        {
            sb.AppendLine();
            sb.AppendLine($"\"{hit.Text}\"");
            sb.AppendLine($"— source: {hit.FileName} (file id: {hit.FileId}), chunk {hit.Ordinal + 1}, collection: {hit.CollectionName}");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Exposes <c>search_knowledge</c> to every module's agent under the <c>knowledge</c> pseudo-module
/// (permission <c>tools.knowledge.search_knowledge</c>). Registered only when <c>Rag:Enabled</c> —
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
        ];
    }
}

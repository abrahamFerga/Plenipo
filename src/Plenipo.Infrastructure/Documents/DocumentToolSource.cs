using Plenipo.Application.Agents;
using Plenipo.Application.Authorization;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Infrastructure.Documents;

/// <summary>
/// Exposes the platform document tools to every module's agent under the <c>documents</c> pseudo-module
/// (permissions <c>tools.documents.*</c>). The <c>ocr_document</c> tool is only offered when the host
/// registered an <see cref="Plenipo.Application.Documents.IOcrEngine"/> — the model never sees a tool
/// this deployment cannot execute.
/// </summary>
public sealed class DocumentToolSource : IPlatformToolSource
{
    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<DocumentTools>();

        var list = new List<ModuleTool>
        {
            Tool("read_document", AIFunctionFactory.Create(tools.ReadDocument, name: "read_document")),
            Tool("generate_pdf", AIFunctionFactory.Create(tools.GeneratePdf, name: "generate_pdf")),
            Tool("list_documents", AIFunctionFactory.Create(tools.ListDocuments, name: "list_documents")),
        };

        if (tools.HasOcrEngine)
        {
            list.Add(Tool("ocr_document", AIFunctionFactory.Create(tools.OcrDocument, name: "ocr_document")));
        }

        return list;
    }

    private static ModuleTool Tool(string name, AIFunction function) => new()
    {
        ModuleId = Permissions.DocumentsToolModule,
        Name = name,
        Permission = Permissions.ForTool(Permissions.DocumentsToolModule, name),
        Function = function,
    };
}

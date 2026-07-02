using Cortex.Application.Authorization;
using Cortex.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Modules.Legal;

/// <summary>Supplies the Legal module's executable tools.</summary>
public sealed class LegalToolSource : IModuleToolSource
{
    public string ModuleId => LegalModule.Id;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var clauses = scopedServices.GetRequiredService<LegalTools>();
        var matters = scopedServices.GetRequiredService<MatterTools>();

        return
        [
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "search_clauses",
                Permission = Permissions.ForTool(ModuleId, "search_clauses"),
                Function = AIFunctionFactory.Create(clauses.SearchClauses, name: "search_clauses"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "draft_clause",
                Permission = Permissions.ForTool(ModuleId, "draft_clause"),
                Function = AIFunctionFactory.Create(clauses.DraftClause, name: "draft_clause"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "create_matter",
                Permission = Permissions.ForTool(ModuleId, "create_matter"),
                Function = AIFunctionFactory.Create(matters.CreateMatter, name: "create_matter"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_matters",
                Permission = Permissions.ForTool(ModuleId, "list_matters"),
                Function = AIFunctionFactory.Create(matters.ListMatters, name: "list_matters"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "attach_document_to_matter",
                Permission = Permissions.ForTool(ModuleId, "attach_document_to_matter"),
                Function = AIFunctionFactory.Create(matters.AttachDocumentToMatter, name: "attach_document_to_matter"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_matter_documents",
                Permission = Permissions.ForTool(ModuleId, "list_matter_documents"),
                Function = AIFunctionFactory.Create(matters.ListMatterDocuments, name: "list_matter_documents"),
            },
        ];
    }
}

using Plenipo.Application.Authorization;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Modules.Finance;

/// <summary>
/// Supplies the Finance module's executable tools, resolving the scoped categorizer (which reads the
/// tenant's learned rules) and the stateless tools from the current request scope.
/// </summary>
public sealed class FinanceToolSource : IModuleToolSource
{
    public string ModuleId => FinanceModule.Id;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var categorizer = scopedServices.GetRequiredService<FinanceCategorizer>();
        var ledger = scopedServices.GetRequiredService<FinanceLedgerTools>();

        return
        [
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "categorize_transaction",
                Permission = Permissions.ForTool(ModuleId, "categorize_transaction"),
                Function = AIFunctionFactory.Create(categorizer.CategorizeTransactionAsync, name: "categorize_transaction"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "summarize_spending",
                Permission = Permissions.ForTool(ModuleId, "summarize_spending"),
                Function = AIFunctionFactory.Create(ledger.SummarizeSpendingAsync, name: "summarize_spending"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "check_budget",
                Permission = Permissions.ForTool(ModuleId, "check_budget"),
                Function = AIFunctionFactory.Create(ledger.CheckBudgetAsync, name: "check_budget"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "record_transaction",
                Permission = Permissions.ForTool(ModuleId, "record_transaction"),
                Function = AIFunctionFactory.Create(ledger.RecordTransactionAsync, name: "record_transaction"),
            },
        ];
    }
}

using Plenipo.Application.Authorization;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Modules.Nutrition;

/// <summary>Supplies the Nutrition module's executable tools.</summary>
public sealed class NutritionToolSource : IModuleToolSource
{
    public string ModuleId => NutritionModule.Id;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<NutritionTools>();
        var diary = scopedServices.GetRequiredService<DiaryTools>();

        return
        [
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "search_foods",
                Permission = Permissions.ForTool(ModuleId, "search_foods"),
                Function = AIFunctionFactory.Create(tools.SearchFoods, name: "search_foods"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "estimate_meal",
                Permission = Permissions.ForTool(ModuleId, "estimate_meal"),
                Function = AIFunctionFactory.Create(tools.EstimateMeal, name: "estimate_meal"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "log_meal",
                Permission = Permissions.ForTool(ModuleId, "log_meal"),
                Function = AIFunctionFactory.Create(diary.LogMeal, name: "log_meal"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "summarize_diary",
                Permission = Permissions.ForTool(ModuleId, "summarize_diary"),
                Function = AIFunctionFactory.Create(diary.SummarizeDiary, name: "summarize_diary"),
            },
        ];
    }
}

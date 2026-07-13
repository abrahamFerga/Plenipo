using Plenipo.Application.Authorization;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Modules.Tasks;

/// <summary>
/// Supplies the Tasks module's executable tools. Each <see cref="ModuleTool"/> binds an
/// <see cref="AIFunction"/> to the permission that gates it; the agent runner filters by that permission
/// before the model call, and the platform audits every invocation.
/// </summary>
public sealed class TasksToolSource : IModuleToolSource
{
    public string ModuleId => TasksModule.Id;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<TasksTools>();

        return
        [
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "list_tasks",
                Permission = Permissions.ForTool(ModuleId, "list_tasks"),
                Function = AIFunctionFactory.Create(tools.ListTasks, name: "list_tasks"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId,
                Name = "add_task",
                Permission = Permissions.ForTool(ModuleId, "add_task"),
                Function = AIFunctionFactory.Create(tools.AddTask, name: "add_task"),
                RequiresApproval = true,
            },
        ];
    }
}

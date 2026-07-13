using Plenipo.Application.Agents;
using Plenipo.Application.Authorization;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Infrastructure.Skills;

/// <summary>
/// Exposes the skill loop to every module's agent under the <c>skills</c> pseudo-module
/// (permissions <c>tools.skills.*</c>). Only registered when <c>Skills:Enabled</c> is true, so a
/// deployment without skills never shows the model these tools. Script execution is marked
/// approval-required — the runner blocks it pending the user's explicit yes.
/// </summary>
public sealed class SkillToolSource : IPlatformToolSource
{
    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<SkillTools>();

        return
        [
            Tool("load_skill", AIFunctionFactory.Create(tools.LoadSkill, name: "load_skill")),
            Tool("read_skill_resource", AIFunctionFactory.Create(tools.ReadSkillResource, name: "read_skill_resource")),
            Tool("run_skill_script", AIFunctionFactory.Create(tools.RunSkillScript, name: "run_skill_script"), requiresApproval: true),
        ];
    }

    private static ModuleTool Tool(string name, AIFunction function, bool requiresApproval = false) => new()
    {
        ModuleId = Permissions.SkillsToolModule,
        Name = name,
        Permission = Permissions.ForTool(Permissions.SkillsToolModule, name),
        Function = function,
        RequiresApproval = requiresApproval,
    };
}

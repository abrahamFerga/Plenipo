using System.ComponentModel;
using Cortex.Application.Agents;
using Cortex.Application.Authorization;
using Cortex.Application.Modules;
using Cortex.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Infrastructure.Handoff;

/// <summary>
/// Generates one <c>ask_{module}</c> tool per installed module, so the model routes by name
/// ("ask finance …" → <c>ask_finance</c>) and each tool's description carries the target
/// module's own description — far better routing signal than a generic (moduleId, question)
/// pair, for small and large models alike. All handoff tools share one permission
/// (<c>tools.handoff.ask_module</c>): granting it enables asking any enabled module; per-run
/// tenant-enablement and the nested RBAC filter still apply inside the handoff.
/// </summary>
public sealed class HandoffToolSource(IModuleCatalog moduleCatalog) : IPlatformToolSource
{
    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var handoff = scopedServices.GetRequiredService<HandoffTools>();
        var permission = Permissions.ForTool(Permissions.HandoffToolModule, "ask_module");

        var tools = new List<ModuleTool>();
        foreach (var manifest in moduleCatalog.Manifests)
        {
            var targetId = manifest.Id;
            var name = $"ask_{targetId}";
            // Capture targetId per tool; the model supplies only the question.
            Task<string> Ask(
                [Description("The question to relay, in the user's own words with all relevant specifics.")] string question,
                CancellationToken cancellationToken = default) =>
                handoff.AskModuleAsync(targetId, question, cancellationToken);

            tools.Add(new ModuleTool
            {
                ModuleId = Permissions.HandoffToolModule,
                Name = name,
                Permission = permission,
                Function = AIFunctionFactory.Create(Ask, name: name,
                    description: $"Ask the {manifest.DisplayName} assistant a read-only question and relay its answer. " +
                                 $"{manifest.Description} Use when the user's request needs information from {manifest.DisplayName}."),
            });
        }

        return tools;
    }
}

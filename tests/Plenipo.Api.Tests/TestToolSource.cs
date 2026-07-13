using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;

namespace Plenipo.Api.Tests;

/// <summary>
/// Supplies the test module's one executable tool (<c>echo</c>) to the tool registry. The agent runner filters
/// this by <see cref="TestModule.EchoPermission"/> before the model call, so a caller without that permission
/// never sees the tool — which is exactly what <c>AgentToolAuthorizationTests</c> pins.
/// </summary>
internal sealed class TestToolSource : IModuleToolSource
{
    public string ModuleId => "test";

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices) =>
    [
        new ModuleTool
        {
            ModuleId = "test",
            Name = "echo",
            Permission = TestModule.EchoPermission,
            RequiresApproval = false,
            Function = AIFunctionFactory.Create(
                (string input) => $"echo: {input}",
                "echo",
                "Echoes the given input back to the caller."),
        },
        new ModuleTool
        {
            ModuleId = "test",
            Name = "record",
            Permission = TestModule.RecordPermission,
            RequiresApproval = true,
            Function = AIFunctionFactory.Create(
                (string value) => $"recorded: {value}",
                "record",
                "Records a value (side-effecting)."),
        },
    ];
}

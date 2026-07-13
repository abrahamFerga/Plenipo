using System.Text.Json;
using Plenipo.Modules.Tasks;

namespace Plenipo.Modules.Tasks.Tests;

/// <summary>
/// Contract + behaviour tests for the worked-example module — a template for what you'd write for your own
/// vertical: assert the manifest declares what you expect, the tool source produces matching executables,
/// the data tab's columns line up with the row JSON, and the tools behave.
/// </summary>
public sealed class TasksModuleTests
{
    [Fact]
    public void Manifest_DeclaresBothTools_WithConventionalPermissions()
    {
        var manifest = new TasksModule().Manifest;

        Assert.Equal("tasks", manifest.Id);
        Assert.Equal(2, manifest.Tools.Count);

        var add = Assert.Single(manifest.Tools, t => t.Name == "add_task");
        Assert.Equal("tools.tasks.add_task", add.Permission);
        Assert.True(add.RequiresApproval); // writing data is gated behind human approval

        var list = Assert.Single(manifest.Tools, t => t.Name == "list_tasks");
        Assert.Equal("tools.tasks.list_tasks", list.Permission);
        Assert.False(list.RequiresApproval);
    }

    [Fact]
    public void DataTab_ColumnFields_ExistOnTheRowJson()
    {
        var tab = Assert.Single(new TasksModule().Manifest.Tabs, t => t.DataEndpoint is not null);
        Assert.Equal("/api/tasks/items", tab.DataEndpoint);

        // The shell renders the data endpoint's rows using these column Fields, so each must be a real
        // (camelCased) property of the row type — a typo would silently render a blank column.
        var json = JsonSerializer.Serialize(
            new TaskItem(Guid.CreateVersion7(), "demo", false),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var row = JsonDocument.Parse(json);
        foreach (var column in tab.Columns)
        {
            Assert.True(
                row.RootElement.TryGetProperty(column.Field, out _),
                $"row JSON has no '{column.Field}' property");
        }
    }

    [Fact]
    public void ToolSource_Tools_MatchTheManifest_AndBindToPermissions()
    {
        var tools = new TasksToolSource().GetTools(new StubProvider(new TaskStore()));

        Assert.Equal(2, tools.Count);
        var add = Assert.Single(tools, t => t.Name == "add_task");
        Assert.Equal("add_task", add.Function.Name);          // executable name matches the descriptor
        Assert.Equal("tools.tasks.add_task", add.Permission);
        Assert.True(add.RequiresApproval);
    }

    [Fact]
    public void AddTask_ThenListTasks_ReflectsTheNewTask()
    {
        var tools = new TasksTools(new TaskStore());

        Assert.Contains("no tasks", tools.ListTasks(), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Buy milk", tools.AddTask("Buy milk"), StringComparison.Ordinal);

        var listed = tools.ListTasks();
        Assert.Contains("Buy milk", listed, StringComparison.Ordinal);
        Assert.Contains("1 task", listed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Minimal provider that supplies only the module's tools class — all <c>GetTools</c> resolves.</summary>
    private sealed class StubProvider(TaskStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(TasksTools) ? new TasksTools(store) : null;
    }
}

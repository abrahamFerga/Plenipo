using Plenipo.Application.Authorization;
using Plenipo.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Modules.Tasks;

/// <summary>
/// The smallest useful Plenipo module: a to-do list. It is the worked example behind
/// <c>/BUILDING_A_MODULE.md</c> and shows the whole module surface in miniature — a manifest (two tools
/// plus a data tab), a read tool, a write tool gated behind human approval, a server-driven table, and
/// per-tool permissions. Copy this folder to start your own vertical.
/// </summary>
public sealed class TasksModule : IModule
{
    /// <summary>Stable module id, used in routes, permissions, and the manifest.</summary>
    public const string Id = "tasks";

    /// <summary>Permission required to view the Tasks tab.</summary>
    public const string ViewTasks = "tasks.items.view";

    public ModuleManifest Manifest { get; } = new()
    {
        Id = Id,
        DisplayName = "Tasks",
        Version = "1.0.0",
        Description = "A minimal to-do module — the worked example for building your own Plenipo vertical.",
        Icon = "check-square",
        AgentInstructions =
            "You are a concise task assistant. Use list_tasks to read the current list and add_task to add one. " +
            "Adding a task needs the user's approval, so never claim a task was added before it is approved.",
        SuggestedPrompts =
        [
            "List my tasks",
            "Add a task to buy groceries",
        ],
        Roles = ["tasks:user", "tasks:admin"],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "list_tasks",
                Description = "List the current tasks and whether each one is done.",
                Permission = Permissions.ForTool(Id, "list_tasks"),
            },
            new ToolDescriptor
            {
                Name = "add_task",
                Description = "Add a new task to the list.",
                Permission = Permissions.ForTool(Id, "add_task"),
                RequiresApproval = true,
            },
        ],
        Tabs =
        [
            new TabDescriptor { Id = "chat", Label = "Chat", Route = "/tasks/chat", Icon = "message-circle", Order = 0 },
            new TabDescriptor
            {
                Id = "tasks",
                Label = "Tasks",
                Route = "/tasks/list",
                Icon = "check-square",
                Order = 1,
                Permission = ViewTasks,
                DataEndpoint = "/api/tasks/items",
                Columns = [new("title", "Task"), new("done", "Done")],
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<TaskStore>();
        services.AddSingleton<TasksTools>();
        services.AddSingleton<IModuleToolSource, TasksToolSource>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Backs the Tasks tab's server-driven table: the platform renders the JSON array as a grid using
        // the tab's Columns, so a list-style tab needs no custom UI.
        var group = endpoints.MapGroup("/api/tasks").WithTags("Tasks").RequireAuthorization();

        group.MapGet("/items", (TaskStore store) => Results.Ok(store.All()))
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewTasks))
            .WithName("Tasks_ListItems");
    }
}

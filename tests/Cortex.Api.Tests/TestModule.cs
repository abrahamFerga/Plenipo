using Cortex.Modules.Sdk;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Api.Tests;

/// <summary>
/// A minimal in-test module so the chat pipeline has a valid, dev-seeded-enabled module to run a turn against.
/// It declares no tools or endpoints — just enough to exercise the agent path end to end (auth → the authorized
/// agent runner → the Mock chat client → a streamed reply → conversation persistence).
/// </summary>
internal sealed class TestModule : IModule
{
    /// <summary>The permission gating the module's <c>echo</c> tool (the conventional tools.&lt;module&gt;.&lt;tool&gt;).</summary>
    public const string EchoPermission = "tools.test.echo";

    /// <summary>The permission gating the module's side-effecting <c>record</c> tool.</summary>
    public const string RecordPermission = "tools.test.record";

    /// <summary>The permission gating the module's admin-console extension page.</summary>
    public const string AdminPagePermission = "test.admin";

    /// <summary>The permission gating the items tab's "retire" row action.</summary>
    public const string RetirePermission = "test.items.retire";

    public ModuleManifest Manifest { get; } = new()
    {
        Id = "test",
        DisplayName = "Test Module",
        Version = "1.0.0",
        AgentInstructions = "You are a test assistant.",
        Tabs =
        [
            new TabDescriptor
            {
                Id = "items", Label = "Items", Route = "/test/items",
                DataEndpoint = "/api/test/items",
                Columns = [new("name", "Name")],
                RowActions =
                [
                    // Ungated: anyone who can see the tab may invoke it.
                    new TabRowAction
                    {
                        Id = "approve", Label = "Approve",
                        EndpointTemplate = "/api/test/items/{id}/approve",
                        Confirm = "Approve this item?",
                    },
                    // Gated: ships only to callers holding the permission.
                    new TabRowAction
                    {
                        Id = "retire", Label = "Retire",
                        EndpointTemplate = "/api/test/items/{id}/retire",
                        Permission = RetirePermission,
                    },
                ],
            },
        ],
        AdminTabs =
        [
            new TabDescriptor
            {
                Id = "widgets", Label = "Widget registry", Route = "/ext/test/widgets",
                Permission = AdminPagePermission,
                DataEndpoint = "/api/test/widgets",
                Columns = [new("name", "Name"), new("status", "Status")],
            },
        ],
        NotificationCategories =
        [
            new("test-alerts", "Test alerts", "Alerts emitted by the test module."),
        ],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "echo",
                Description = "Echoes the given input back to the caller.",
                Permission = EchoPermission,
                RequiresApproval = false,
            },
            new ToolDescriptor
            {
                Name = "record",
                Description = "Records a value (side-effecting — requires human approval).",
                Permission = RecordPermission,
                RequiresApproval = true,
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // No module services needed for the pipeline test.
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No module endpoints needed for the pipeline test.
    }
}

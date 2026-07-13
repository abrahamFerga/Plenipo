using System.Net.Http.Json;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// End-to-end coverage of the per-tenant module kill-switch. An operator can disable a module for a tenant, and
/// the agent runner must then refuse chat to it (the same switch also 404s the module's HTTP endpoints and hides
/// it from the workspace). Re-enabling restores access. Uses its own factory instance so toggling the module
/// doesn't affect other test classes.
/// </summary>
public sealed class ModuleEnablementTests : IClassFixture<PlenipoApiFactory>
{
    private readonly PlenipoApiFactory _factory;

    public ModuleEnablementTests(PlenipoApiFactory factory) => _factory = factory;

    private HttpClient Operator(string subject)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin"); // ManageModules + chat.use
        client.DefaultRequestHeaders.Add("X-Dev-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        return client;
    }

    private async Task<List<StreamEvent>> ChatAsync(HttpClient client, string message)
    {
        var response = await client.PostAsJsonAsync("/api/chat/stream", new { moduleId = "test", message });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<StreamEvent>>())!;
    }

    [Fact]
    public async Task Chat_is_refused_when_the_module_is_disabled_and_restored_when_re_enabled()
    {
        var admin = Operator("module-admin");

        // Disable the module for this tenant.
        (await admin.PutAsJsonAsync("/api/admin/modules/test", new { enabled = false })).EnsureSuccessStatusCode();

        var refused = await ChatAsync(admin, "Hello");
        Assert.Contains(refused, e =>
            e.Type == "Error" && (e.Error ?? string.Empty).Contains("not enabled", StringComparison.OrdinalIgnoreCase));

        // Re-enable it — chat works again.
        (await admin.PutAsJsonAsync("/api/admin/modules/test", new { enabled = true })).EnsureSuccessStatusCode();

        var allowed = await ChatAsync(admin, "Hello again");
        Assert.Contains(allowed, e => e.Type == "Completed");
        Assert.DoesNotContain(allowed, e => e.Type == "Error");
    }

    private sealed record StreamEvent(string Type, string? Text, string? ToolName, Guid? ConversationId, string? Error);
}

using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Agents;
using Plenipo.Application.Authorization;
using Plenipo.Application.Modules;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Testing.Conformance;

/// <summary>
/// Manifest integrity, proved against the running host rather than by reading source: the tools
/// the manifest declares are the tools the tool source builds, with the same permission string and
/// the same approval flag; every permission follows the platform convention so the security catalog
/// can grant it; and the catalog the admin console renders agrees with both. A new tool needs three
/// things — the descriptor, the executable, and one permission string in both — and this is where
/// a missing one shows up instead of as a tool that is never called and never errors.
/// </summary>
public abstract class PlenipoManifestConformance<TProgram>(PlenipoHostFixture<TProgram> fixture)
    where TProgram : class
{
    private ProductContract Contract => fixture.Contract;

    [Fact]
    public void The_contract_names_tools_the_manifest_declares()
    {
        var manifest = Manifest();

        var read = Assert.Single(manifest.Tools, t => string.Equals(t.Name, Contract.ReadTool, StringComparison.Ordinal));
        Assert.False(read.RequiresApproval, $"ProductContract.ReadTool '{Contract.ReadTool}' must not require approval.");

        var write = Assert.Single(manifest.Tools, t => string.Equals(t.Name, Contract.WriteTool, StringComparison.Ordinal));
        Assert.True(write.RequiresApproval, $"ProductContract.WriteTool '{Contract.WriteTool}' must be declared RequiresApproval = true.");
    }

    [Fact]
    public void Every_tool_permission_follows_the_platform_convention()
    {
        var manifest = Manifest();

        foreach (var tool in manifest.Tools)
        {
            Assert.Equal(Permissions.ForTool(manifest.Id, tool.Name), tool.Permission);
        }
    }

    [Fact]
    public async Task Every_manifest_tool_has_an_executable_twin_with_the_same_permission_and_approval_flag()
    {
        var manifest = Manifest();

        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using (scope)
        {
            var registry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
            var executable = registry.GetModuleTools(manifest.Id, scope.ServiceProvider)
                .Where(t => string.Equals(t.ModuleId, manifest.Id, StringComparison.Ordinal))
                .ToDictionary(t => t.Name, StringComparer.Ordinal);

            foreach (var declared in manifest.Tools)
            {
                Assert.True(executable.TryGetValue(declared.Name, out var twin),
                    $"Manifest tool '{declared.Name}' has no ModuleTool in the module's IModuleToolSource — it can never be called.");
                Assert.True(string.Equals(declared.Permission, twin!.Permission, StringComparison.Ordinal),
                    $"Tool '{declared.Name}': the manifest declares permission '{declared.Permission}' but the ModuleTool carries '{twin.Permission}'. Use Permissions.ForTool in both.");
                // The runner gates a tool when EITHER the manifest or the ModuleTool says so, so the
                // manifest's flag is the source of truth for module tools and a ModuleTool that omits
                // it is still gated. The drift that matters is the other direction: a ModuleTool that
                // gates a tool the manifest does not — the security catalog and the UI, which read
                // the manifest, would then show a write as ungated.
                Assert.True(declared.RequiresApproval || !twin.RequiresApproval,
                    $"Tool '{declared.Name}': the ModuleTool carries RequiresApproval = true but the manifest declares it ungated, " +
                    "so the security catalog and the UI misreport it. Declare RequiresApproval on the ToolDescriptor.");
            }

            var undeclared = executable.Keys.Except(manifest.Tools.Select(t => t.Name), StringComparer.Ordinal).ToArray();
            Assert.True(undeclared.Length == 0,
                $"Executable tools missing from the manifest (the platform reads the manifest first): {string.Join(", ", undeclared)}");
        }
    }

    [Fact]
    public async Task The_security_catalog_lists_every_manifest_tool_with_its_approval_flag()
    {
        var manifest = Manifest();

        using var admin = fixture.AdminClient();
        var catalog = await admin.GetFromJsonAsync<JsonElement>("/api/admin/security/catalog");
        var module = Assert.Single(catalog.GetProperty("modules").EnumerateArray(),
            m => string.Equals(m.GetProperty("id").GetString(), manifest.Id, StringComparison.Ordinal));
        var listed = module.GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("permission").GetString() ?? "", t => t.GetProperty("requiresApproval").GetBoolean(), StringComparer.Ordinal);

        foreach (var tool in manifest.Tools)
        {
            Assert.True(listed.TryGetValue(tool.Permission, out var requiresApproval),
                $"'{tool.Permission}' is absent from /api/admin/security/catalog, so an admin cannot grant it.");
            Assert.Equal(tool.RequiresApproval, requiresApproval);
        }
    }

    private ModuleManifest Manifest()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IModuleCatalog>();
        Assert.True(catalog.TryGetManifest(Contract.ModuleId, out var manifest),
            $"Module '{Contract.ModuleId}' is not installed — check ProductContract.ModuleId against the manifest id.");
        return manifest!;
    }
}

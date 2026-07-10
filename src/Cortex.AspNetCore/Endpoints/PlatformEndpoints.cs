using Cortex.Application.Ai;
using Cortex.Application.Files;
using Cortex.Application.Modules;
using Cortex.Application.Skills;
using Cortex.Core.Identity;
using Cortex.Modules.Sdk;
using Microsoft.Extensions.Options;

namespace Cortex.AspNetCore.Endpoints;

/// <summary>
/// Platform-level endpoints that drive the dashboard shell: the modules/tabs the current user can see,
/// and the caller's own identity and permissions.
/// </summary>
public static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform").WithTags("Platform").RequireAuthorization();

        group.MapGet("/modules", async (
            IModuleCatalog catalog, ITenantModuleStore moduleStore, ICurrentUser user,
            Cortex.Infrastructure.Persistence.PlatformDbContext db, ISkillCatalog skills, CancellationToken ct) =>
        {
            // A module is visible unless a tenant admin has explicitly disabled it for this tenant (default-on).
            var disabled = await moduleStore.GetDisabledModuleIdsAsync(ct);

            // The tenant's admin-created agent profiles (tenant-scoped via the query filter) merge
            // into each module's agent picker; a profile overrides a manifest agent of the same name.
            var profiles = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.AgentProfiles, ct);

            var modules = catalog.Manifests
                .Where(m => !disabled.Contains(m.Id))
                .Select(m => ToDto(m, user, profiles.Where(p => p.ModuleId == m.Id), skills.List(m.Id)))
                .ToList();
            return Results.Ok(modules);
        })
        .WithName("Platform_GetModules");

        group.MapGet("/me", (ICurrentUser user) => Results.Ok(new MeDto(
            user.UserId,
            user.DisplayName,
            user.TenantId,
            user.Permissions.OrderBy(p => p, StringComparer.Ordinal).ToArray())))
        .WithName("Platform_GetMe");

        // The product's public identity, resolved at RUNTIME from host configuration
        // (Branding:ProductName). This is what lets ONE prebuilt @cortex/ui bundle serve every
        // product — the shell asks the host who it is instead of baking the name in at build
        // time. Anonymous on purpose: the name must render before any sign-in.
        group.MapGet("/branding", (IConfiguration configuration) =>
                Results.Ok(new BrandingDto(configuration["Branding:ProductName"] ?? "Cortex")))
            .AllowAnonymous()
            .WithName("Platform_Branding");

        // Deployment-level facts the shell uses to set expectations (e.g. a "demo mode" banner when the
        // chat assistant is running on the dependency-free Mock provider rather than a real LLM).
        group.MapGet("/info", (IOptions<AiOptions> ai, IOptions<FileStorageOptions> files) =>
        {
            var options = ai.Value;
            return Results.Ok(new PlatformInfoDto(
                ChatEnabled: options.IsEnabled,
                DemoMode: string.Equals(options.Provider, AiProviders.Mock, StringComparison.OrdinalIgnoreCase),
                // Published so the composer can refuse an oversized attachment BEFORE uploading —
                // the same limit FileEndpoints enforces with a 413.
                MaxUploadBytes: files.Value.MaxUploadBytes,
                // The chat's model picker choices; the runner enforces the same list per turn.
                AvailableModels: options.AvailableModels.ToArray()));
        })
        .WithName("Platform_GetInfo");
    }

    private static ModuleDto ToDto(
        ModuleManifest manifest, ICurrentUser user, IEnumerable<Cortex.Core.Platform.AgentProfile> tenantProfiles,
        IReadOnlyList<SkillSummary> skills)
    {
        // The agent picker's entries: tenant profiles first (they win a name collision), then
        // manifest agents not overridden. When a tenant profile is the default, no manifest agent
        // may also claim default — the runner resolves in exactly that order.
        var profileList = tenantProfiles.ToList();
        var profileNames = profileList.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var tenantHasDefault = profileList.Any(p => p.IsDefault);
        var agents = profileList
            .Select(p => new ModuleAgentDto(p.Name, null, p.IsDefault, p.Model))
            .Concat(manifest.Agents
                .Where(a => !profileNames.Contains(a.Name))
                .Select(a => new ModuleAgentDto(a.Name, a.Description, a.IsDefault && !tenantHasDefault, a.Model)))
            // Workflows share the picker (and the run request's Agent field); the description
            // prefix is how the UI tells a chain from a single agent.
            .Concat(manifest.Workflows
                .Select(w => new ModuleAgentDto(w.Name, $"Workflow · {w.Description}", false, null)))
            .ToArray();
        var tabs = manifest.Tabs
            .Where(t => t.Permission is null || user.HasPermission(t.Permission))
            .OrderBy(t => t.Order)
            .Select(t => new TabDto(
                t.Id, t.Label, t.Route, t.Icon, t.DataEndpoint,
                t.Columns.Select(c => new TabColumnDto(c.Field, c.Header)).ToArray(),
                t.Placeholder,
                // The editor ships only to callers holding its permission, so the payload never
                // advertises affordances the user can't use (the endpoints stay gated regardless).
                t.Editor is { } e && user.HasPermission(e.Permission)
                    ? new TabEditorDto(
                        e.UpsertEndpoint, e.DeleteEndpoint, e.KeyField,
                        e.Fields.Select(f => new TabEditorFieldDto(f.Field, f.Label, f.Multiline, f.Required, f.Numeric)).ToArray())
                    : null,
                t.DetailEndpoint,
                t.Chart is { } chart
                    ? new TabChartDto(chart.XField, chart.YField, chart.SeriesField, chart.YLabel)
                    : null,
                // Same rule as the editor: only advertise actions the caller can actually invoke.
                t.Actions
                    .Where(a => a.Permission is null || user.HasPermission(a.Permission))
                    .Select(a => new TabActionDto(a.Id, a.Label, a.Endpoint, a.Confirm))
                    .ToArray()))
            .ToArray();

        return new ModuleDto(
            manifest.Id, manifest.DisplayName, manifest.Description, manifest.Icon, tabs,
            manifest.SuggestedPrompts.ToArray(), agents,
            // Slash-invocable in this module's chat: the global library + the module's own bundles.
            skills.Select(s => new ModuleSkillDto(s.Name, s.Description)).ToArray());
    }

    private sealed record ModuleDto(
        string Id, string DisplayName, string? Description, string? Icon, TabDto[] Tabs, string[] SuggestedPrompts,
        ModuleAgentDto[] Agents, ModuleSkillDto[] Skills);

    /// <summary>A skill the composer's "/" autocomplete offers for this module.</summary>
    private sealed record ModuleSkillDto(string Name, string Description);

    /// <summary>An entry in the chat's agent picker: a tenant profile or a module-shipped agent.</summary>
    private sealed record ModuleAgentDto(string Name, string? Description, bool IsDefault, string? Model);

    private sealed record TabDto(
        string Id, string Label, string Route, string? Icon, string? DataEndpoint, TabColumnDto[] Columns, string? Placeholder,
        TabEditorDto? Editor, string? DetailEndpoint, TabChartDto? Chart, TabActionDto[] Actions);

    private sealed record TabChartDto(string XField, string YField, string? SeriesField, string? YLabel);

    private sealed record TabActionDto(string Id, string Label, string Endpoint, string? Confirm);

    private sealed record TabColumnDto(string Field, string Header);

    private sealed record TabEditorDto(string UpsertEndpoint, string? DeleteEndpoint, string? KeyField, TabEditorFieldDto[] Fields);

    private sealed record TabEditorFieldDto(string Field, string Label, bool Multiline, bool Required, bool Numeric);

    private sealed record MeDto(Guid? UserId, string? DisplayName, Guid? TenantId, string[] Permissions);

    /// <summary>The host's product identity; extensible (logo URL, accent color) without a breaking change.</summary>
    private sealed record BrandingDto(string Name);

    private sealed record PlatformInfoDto(
        bool ChatEnabled, bool DemoMode, long MaxUploadBytes, string[] AvailableModels);
}

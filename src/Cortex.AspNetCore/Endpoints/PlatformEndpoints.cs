using Cortex.Application.Ai;
using Cortex.Application.Files;
using Cortex.Application.Modules;
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

        group.MapGet("/modules", async (IModuleCatalog catalog, ITenantModuleStore moduleStore, ICurrentUser user, CancellationToken ct) =>
        {
            // A module is visible unless a tenant admin has explicitly disabled it for this tenant (default-on).
            var disabled = await moduleStore.GetDisabledModuleIdsAsync(ct);
            var modules = catalog.Manifests
                .Where(m => !disabled.Contains(m.Id))
                .Select(m => ToDto(m, user))
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
                MaxUploadBytes: files.Value.MaxUploadBytes));
        })
        .WithName("Platform_GetInfo");
    }

    private static ModuleDto ToDto(ModuleManifest manifest, ICurrentUser user)
    {
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
                t.DetailEndpoint))
            .ToArray();

        return new ModuleDto(
            manifest.Id, manifest.DisplayName, manifest.Description, manifest.Icon, tabs,
            manifest.SuggestedPrompts.ToArray());
    }

    private sealed record ModuleDto(
        string Id, string DisplayName, string? Description, string? Icon, TabDto[] Tabs, string[] SuggestedPrompts);

    private sealed record TabDto(
        string Id, string Label, string Route, string? Icon, string? DataEndpoint, TabColumnDto[] Columns, string? Placeholder,
        TabEditorDto? Editor, string? DetailEndpoint);

    private sealed record TabColumnDto(string Field, string Header);

    private sealed record TabEditorDto(string UpsertEndpoint, string? DeleteEndpoint, string? KeyField, TabEditorFieldDto[] Fields);

    private sealed record TabEditorFieldDto(string Field, string Label, bool Multiline, bool Required, bool Numeric);

    private sealed record MeDto(Guid? UserId, string? DisplayName, Guid? TenantId, string[] Permissions);

    private sealed record PlatformInfoDto(bool ChatEnabled, bool DemoMode, long MaxUploadBytes);
}

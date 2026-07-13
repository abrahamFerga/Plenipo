using Plenipo.Application.Authorization;
using Plenipo.Application.Connectors;
using Plenipo.Core.Multitenancy;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Connectors;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.AspNetCore.Endpoints;

/// <summary>
/// The admin Integrations surface: list installed connectors with the caller-tenant's state,
/// enable/disable them (default-off, both audited via the entity-change audit), and write
/// schema-driven settings. Secrets are write-only: reads report only that a value exists.
/// </summary>
public static class ConnectorAdminEndpoints
{
    public static void MapConnectorAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/connectors")
            .WithTags("Admin")
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageConnectors));

        // Installed connectors + this tenant's enablement/settings state, schema included so the
        // admin UI renders the settings form without knowing any connector specifics. Alongside
        // them, the "available" list: first-party connectors this deployment did NOT install,
        // with the package + registration call an operator needs — the browsable marketplace.
        group.MapGet("/", async (
                IConnectorCatalog catalog, ConnectorSettingsService settings, PlatformDbContext db,
                CancellationToken cancellationToken) =>
            {
                var rows = await db.TenantConnectors.ToListAsync(cancellationToken);
                var installed = catalog.Manifests
                    .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(m =>
                    {
                        var row = rows.FirstOrDefault(r => string.Equals(r.ConnectorId, m.Id, StringComparison.Ordinal));
                        var withValues = settings.KeysWithValues(row);
                        return new ConnectorDto(
                            m.Id, m.DisplayName, m.Description, m.AuthMode.ToString(), m.SupportsSync, m.Icon,
                            row?.Enabled ?? false,
                            m.Settings.Select(s => new ConnectorSettingDto(
                                s.Key, s.Label, s.Description, s.Required, s.IsSecret,
                                HasValue: withValues.Contains(s.Key))).ToArray(),
                            m.Tools.Select(t => new ConnectorToolDto(t.Name, t.Description, t.Permission, t.RequiresApproval)).ToArray());
                    })
                    .ToArray();
                var available = ConnectorDirectory.All
                    .Where(k => !catalog.TryGetManifest(k.Id, out _))
                    .Select(k => new AvailableConnectorDto(k.Id, k.DisplayName, k.Description, k.Package, k.Registration))
                    .ToArray();
                return Results.Ok(new ConnectorCatalogDto(installed, available));
            })
            .WithName("Admin_Connectors");

        group.MapPost("/{connectorId}/enable", async (
                string connectorId, IConnectorCatalog catalog, PlatformDbContext db, ITenantContext tenant,
                CancellationToken cancellationToken) =>
            {
                if (!catalog.TryGetManifest(connectorId, out _))
                {
                    return Results.NotFound();
                }

                var row = await db.TenantConnectors
                    .FirstOrDefaultAsync(c => c.ConnectorId == connectorId, cancellationToken);
                if (row is null)
                {
                    row = new TenantConnector { TenantId = tenant.RequireTenantId(), ConnectorId = connectorId };
                    db.TenantConnectors.Add(row);
                }

                row.Enabled = true;
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok();
            })
            .WithName("Admin_EnableConnector");

        // Disabling turns the connector's tools off for the tenant immediately (the runner checks
        // per turn) AND revokes every user's stored OAuth session — re-enabling forces re-auth.
        // Settings are kept: re-enabling doesn't force re-entering everything.
        group.MapPost("/{connectorId}/disable", async (
                string connectorId, PlatformDbContext db, CancellationToken cancellationToken) =>
            {
                var row = await db.TenantConnectors
                    .FirstOrDefaultAsync(c => c.ConnectorId == connectorId, cancellationToken);
                if (row is null)
                {
                    return Results.NotFound();
                }

                row.Enabled = false;
                var logins = await db.UserConnectorLogins
                    .Where(l => l.ConnectorId == connectorId)
                    .ToListAsync(cancellationToken);
                db.UserConnectorLogins.RemoveRange(logins);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { revokedLogins = logins.Count });
            })
            .WithName("Admin_DisableConnector");

        // Schema-driven settings write. Omitted or null values keep what's stored (the UI never has
        // the secret to echo back); empty string clears a value; secrets are protected at rest.
        group.MapPut("/{connectorId}/settings", async (
                string connectorId, UpdateConnectorSettingsRequest body,
                IConnectorCatalog catalog, ConnectorSettingsService settings, PlatformDbContext db,
                ITenantContext tenant, CancellationToken cancellationToken) =>
            {
                if (!catalog.TryGetManifest(connectorId, out var manifest) || manifest is null)
                {
                    return Results.NotFound();
                }

                var unknown = body.Values.Keys
                    .Where(k => !manifest.Settings.Any(s => string.Equals(s.Key, k, StringComparison.Ordinal)))
                    .ToList();
                if (unknown.Count > 0)
                {
                    return Results.BadRequest(new { error = $"Unknown setting(s): {string.Join(", ", unknown)}." });
                }

                var row = await db.TenantConnectors
                    .FirstOrDefaultAsync(c => c.ConnectorId == connectorId, cancellationToken);
                if (row is null)
                {
                    row = new TenantConnector { TenantId = tenant.RequireTenantId(), ConnectorId = connectorId };
                    db.TenantConnectors.Add(row);
                }

                await settings.SaveAsync(row, manifest, body.Values, cancellationToken);
                return Results.Ok();
            })
            .WithName("Admin_UpdateConnectorSettings");
    }

    private sealed record ConnectorCatalogDto(
        IReadOnlyList<ConnectorDto> Installed, IReadOnlyList<AvailableConnectorDto> Available);

    private sealed record AvailableConnectorDto(
        string Id, string DisplayName, string Description, string Package, string Registration);

    private sealed record ConnectorDto(
        string Id, string DisplayName, string Description, string AuthMode, bool SupportsSync, string? Icon,
        bool Enabled, IReadOnlyList<ConnectorSettingDto> Settings, IReadOnlyList<ConnectorToolDto> Tools);

    private sealed record ConnectorSettingDto(
        string Key, string Label, string? Description, bool Required, bool IsSecret, bool HasValue);

    private sealed record ConnectorToolDto(string Name, string? Description, string Permission, bool RequiresApproval);

    /// <summary>Setting key → new value. Omit (or send null for) a key to keep its stored value.</summary>
    public sealed record UpdateConnectorSettingsRequest(Dictionary<string, string?> Values);
}

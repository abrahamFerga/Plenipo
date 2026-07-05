using Cortex.Application.Ai;
using Cortex.Application.Auditing;
using Cortex.Application.Authorization;
using Cortex.Application.Modules;
using Cortex.Application.Usage;
using Cortex.AspNetCore.Setup;
using Cortex.Core.Identity;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cortex.AspNetCore.Endpoints;

/// <summary>
/// The security / RBAC admin surface that backs the configuration dashboard. Exposes the full
/// permission map (platform permissions + every module tool and the permission it requires), the role
/// model, per-user role and permission management, and read access to the audit log and token-usage
/// telemetry. Every route is gated on a platform-administration permission.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization();

        MapSecurity(group);
        MapUsers(group);
        MapModules(group);
        MapTenants(group);
        MapAiSettings(group);
        MapNotificationSettings(group);
        MapOps(group);
        MapAgentProfiles(group);
        MapAuditAndUsage(group);
    }

    // ── Ops overview: one tenant-scoped health snapshot (platform.audit.view) ──

    private static void MapOps(RouteGroupBuilder group)
    {
        // Everything an admin checks when "something feels slow": queue depth and failures,
        // connector sync recency, knowledge-index freshness, budget posture. Read-only
        // aggregation over tenant-scoped tables — one call for the whole picture.
        group.MapGet("/ops", async (
            PlatformDbContext db, ITokenUsageReader usage, ITenantAiSettings aiSettings,
            IOptions<AiOptions> ai, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var dayAgo = now.AddHours(-24);

            var jobCounts = await db.BackgroundJobs
                .GroupBy(j => j.Status)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var oldestQueued = await db.BackgroundJobs
                .Where(j => j.Status == JobStatus.Queued)
                .OrderBy(j => j.CreatedAt)
                .Select(j => (DateTimeOffset?)j.CreatedAt)
                .FirstOrDefaultAsync(ct);
            var failed24h = await db.BackgroundJobs
                .CountAsync(j => j.Status == JobStatus.Failed && j.CompletedAt >= dayAgo, ct);

            var connectors = await db.TenantConnectors
                .Where(c => c.Enabled)
                .Select(c => new OpsConnectorDto(
                    c.ConnectorId,
                    db.ConnectorBindings.Count(b => b.ConnectorId == c.ConnectorId),
                    db.ConnectorBindings.Where(b => b.ConnectorId == c.ConnectorId)
                        .Max(b => (DateTimeOffset?)b.LastSyncedAt)))
                .ToListAsync(ct);

            var ragCollections = await db.RagCollections.CountAsync(ct);
            var ragChunks = await db.RagChunks.CountAsync(ct);
            var lastIngestAt = await db.RagChunks.MaxAsync(c => (DateTimeOffset?)c.CreatedAt, ct);

            var webhookConfigured = await db.NotificationSettings
                .AnyAsync(s => s.WebhookUrl != null, ct);

            var effective = await aiSettings.ResolveAsync(ct);
            var monthTokens = await usage.GetTenantMonthTotalAsync(now, ct);

            return Results.Ok(new OpsDto(
                new OpsJobsDto(
                    Queued: jobCounts.FirstOrDefault(c => c.Status == JobStatus.Queued)?.Count ?? 0,
                    Running: jobCounts.FirstOrDefault(c => c.Status == JobStatus.Running)?.Count ?? 0,
                    Failed24h: failed24h,
                    OldestQueuedAgeSeconds: oldestQueued is { } t ? (long)(now - t).TotalSeconds : null),
                connectors,
                new OpsRagDto(ragCollections, ragChunks, lastIngestAt),
                new OpsNotificationsDto(webhookConfigured),
                new OpsAiDto(ai.Value.Provider, ai.Value.Model, monthTokens, effective.MaxMonthlyTokens)));
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ViewAuditLog))
        .WithName("Admin_Ops");
    }

    private sealed record OpsJobsDto(int Queued, int Running, int Failed24h, long? OldestQueuedAgeSeconds);
    private sealed record OpsConnectorDto(string ConnectorId, int BindingCount, DateTimeOffset? LastSyncedAt);
    private sealed record OpsRagDto(int Collections, int Chunks, DateTimeOffset? LastIngestAt);
    private sealed record OpsNotificationsDto(bool WebhookConfigured);
    private sealed record OpsAiDto(string Provider, string Model, long MonthTokens, long MaxMonthlyTokens);
    private sealed record OpsDto(
        OpsJobsDto Jobs, IReadOnlyList<OpsConnectorDto> Connectors, OpsRagDto Rag,
        OpsNotificationsDto Notifications, OpsAiDto Ai);

    // ── Notification delivery settings (platform.notifications.manage) ──────

    private static void MapNotificationSettings(RouteGroupBuilder group)
    {
        // The webhook URL plus whether a signing secret is set — never the secret itself.
        group.MapGet("/notification-settings", async (PlatformDbContext db, CancellationToken ct) =>
        {
            var row = await db.NotificationSettings.FirstOrDefaultAsync(ct);
            return Results.Ok(new NotificationSettingsDto(row?.WebhookUrl, row?.WebhookSecretRef is not null));
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageNotifications))
        .WithName("Admin_NotificationSettings");

        // Write-only secret contract: null keeps the stored secret, "" clears it, a value replaces
        // it (old vault entry forgotten best-effort). Clearing the URL disables webhook delivery.
        group.MapPut("/notification-settings", async (
            [FromBody] NotificationSettingsRequest body, PlatformDbContext db,
            Cortex.Application.Secrets.ISecretVault vault, ICurrentUser current, CancellationToken ct) =>
        {
            if (current.TenantId is not Guid tenantId)
            {
                return Results.BadRequest("No tenant context.");
            }

            var url = string.IsNullOrWhiteSpace(body.WebhookUrl) ? null : body.WebhookUrl.Trim();
            if (url is not null && !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                return Results.BadRequest("webhookUrl must be an absolute URL.");
            }

            var row = await db.NotificationSettings.FirstOrDefaultAsync(ct)
                ?? db.NotificationSettings.Add(new NotificationSettings { TenantId = tenantId }).Entity;
            row.WebhookUrl = url;

            if (body.WebhookSecret is not null)
            {
                var previous = row.WebhookSecretRef;
                row.WebhookSecretRef = body.WebhookSecret.Length == 0
                    ? null
                    : await vault.StoreAsync(WebhookSecretScope, body.WebhookSecret, ct);
                if (previous is not null)
                {
                    await vault.ForgetAsync(previous, ct);
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageNotifications))
        .WithName("Admin_SetNotificationSettings");
    }

    /// <summary>Must match WebhookNotificationChannel.SecretScope (Cortex.Infrastructure).</summary>
    private const string WebhookSecretScope = "Cortex.Notifications.WebhookSecret";

    // ── Agent profiles: named, per-module chatbot configurations (platform.ai.manage) ──

    private static void MapAgentProfiles(RouteGroupBuilder group)
    {
        group.MapGet("/agent-profiles", async (string? moduleId, PlatformDbContext db, CancellationToken ct) =>
        {
            var query = db.AgentProfiles.AsQueryable();
            if (!string.IsNullOrWhiteSpace(moduleId))
            {
                query = query.Where(p => p.ModuleId == moduleId);
            }

            var profiles = await query
                .OrderBy(p => p.ModuleId).ThenBy(p => p.Name)
                .Select(p => new AgentProfileDto(p.Id, p.ModuleId, p.Name, p.Instructions, p.Mode.ToString(), p.IsDefault))
                .ToListAsync(ct);
            return Results.Ok(profiles);
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageAiSettings))
        .WithName("Admin_AgentProfiles");

        // Upsert by (moduleId, name). Making a profile default atomically demotes the module's
        // previous default — the partial unique index backstops the invariant.
        group.MapPut("/agent-profiles", async (
            [FromBody] AgentProfileRequest body, PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            if (current.TenantId is not Guid tenantId)
            {
                return Results.BadRequest("No tenant context.");
            }

            var moduleId = body.ModuleId?.Trim().ToLowerInvariant();
            var name = body.Name?.Trim();
            var instructions = body.Instructions?.Trim();
            if (string.IsNullOrWhiteSpace(moduleId) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(instructions))
            {
                return Results.BadRequest("moduleId, name, and instructions are required.");
            }

            if (instructions.Length > TenantAiSettingsValidator.MaxSystemPromptLength)
            {
                return Results.BadRequest($"instructions must be {TenantAiSettingsValidator.MaxSystemPromptLength:N0} characters or fewer.");
            }

            if (!Enum.TryParse<AgentProfileMode>(body.Mode, ignoreCase: true, out var mode))
            {
                mode = AgentProfileMode.Append;
            }

            if (body.IsDefault)
            {
                var currentDefaults = await db.AgentProfiles
                    .Where(p => p.ModuleId == moduleId && p.IsDefault && p.Name != name)
                    .ToListAsync(ct);
                foreach (var d in currentDefaults)
                {
                    d.IsDefault = false;
                }
            }

            var profile = await db.AgentProfiles.FirstOrDefaultAsync(p => p.ModuleId == moduleId && p.Name == name, ct);
            if (profile is null)
            {
                profile = new AgentProfile
                {
                    TenantId = tenantId,
                    ModuleId = moduleId,
                    Name = name,
                    Instructions = instructions,
                    Mode = mode,
                    IsDefault = body.IsDefault,
                };
                db.AgentProfiles.Add(profile);
            }
            else
            {
                profile.Instructions = instructions;
                profile.Mode = mode;
                profile.IsDefault = body.IsDefault;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new AgentProfileDto(profile.Id, profile.ModuleId, profile.Name, profile.Instructions, profile.Mode.ToString(), profile.IsDefault));
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageAiSettings))
        .WithName("Admin_UpsertAgentProfile");

        // Provenance lookup: resolve an assistant message's InstructionsHash to the exact
        // instruction text the turn ran under (profiles, manifest, system prompt, skills — the
        // whole assembly, byte-identical).
        group.MapGet("/instruction-snapshots/{hash}", async (string hash, PlatformDbContext db, CancellationToken ct) =>
        {
            var normalized = hash.ToLowerInvariant();
            var snapshot = await db.InstructionSnapshots
                .FirstOrDefaultAsync(s => s.Hash == normalized, ct);
            return snapshot is null
                ? Results.NotFound()
                : Results.Ok(new InstructionSnapshotDto(snapshot.Hash, snapshot.Instructions, snapshot.CreatedAt));
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageAiSettings))
        .WithName("Admin_GetInstructionSnapshot");

        group.MapDelete("/agent-profiles/{id:guid}", async (Guid id, PlatformDbContext db, CancellationToken ct) =>
        {
            var profile = await db.AgentProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (profile is null)
            {
                return Results.NotFound();
            }

            db.AgentProfiles.Remove(profile);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageAiSettings))
        .WithName("Admin_DeleteAgentProfile");
    }

    // ── Per-tenant AI settings (platform.ai.manage) ──────────────────────────

    private static void MapAiSettings(RouteGroupBuilder group)
    {
        // The tenant's AI overrides plus the deployment defaults (so the UI can show "override or default").
        group.MapGet("/ai-settings", async (PlatformDbContext db, IOptions<AiOptions> ai, CancellationToken ct) =>
        {
            var row = await db.TenantAiSettings.FirstOrDefaultAsync(ct);
            var defaults = ai.Value;
            return Results.Ok(new AiSettingsDto(
                row?.SystemPrompt, row?.MaxConversationTokens, row?.MaxMonthlyTokens,
                defaults.SystemPrompt, defaults.MaxConversationTokens, defaults.MaxMonthlyTokens));
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageAiSettings))
        .WithName("Admin_AiSettings");

        // Set the tenant's AI overrides. Null/blank fields clear the override (fall back to the default). The
        // agent runner applies these each turn; the change is auto-audited as an entity change.
        group.MapPut("/ai-settings", async (
            [FromBody] AiSettingsRequest body, PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            if (current.TenantId is not Guid tenantId)
            {
                return Results.BadRequest("No tenant context.");
            }

            // Validate the value we will actually store (trimmed), so a length check reflects the persisted prompt.
            var systemPrompt = string.IsNullOrWhiteSpace(body.SystemPrompt) ? null : body.SystemPrompt.Trim();
            if (TenantAiSettingsValidator.Validate(systemPrompt, body.MaxConversationTokens, body.MaxMonthlyTokens) is { } error)
            {
                return Results.BadRequest(error);
            }

            var row = await db.TenantAiSettings.FirstOrDefaultAsync(ct);
            if (row is null)
            {
                db.TenantAiSettings.Add(new TenantAiSettings
                {
                    TenantId = tenantId,
                    SystemPrompt = systemPrompt,
                    MaxConversationTokens = body.MaxConversationTokens,
                    MaxMonthlyTokens = body.MaxMonthlyTokens,
                });
            }
            else
            {
                row.SystemPrompt = systemPrompt;
                row.MaxConversationTokens = body.MaxConversationTokens;
                row.MaxMonthlyTokens = body.MaxMonthlyTokens;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageAiSettings))
        .WithName("Admin_SetAiSettings");
    }

    // ── Tenant administration (cross-tenant; operator-only) ──────────────────

    private static void MapTenants(RouteGroupBuilder group)
    {
        // All tenants. The Tenant table is not tenant-owned, so this is a cross-tenant operator view.
        group.MapGet("/tenants", async (PlatformDbContext db, CancellationToken ct) =>
        {
            var tenants = await db.Tenants
                .OrderBy(t => t.Name)
                .Select(t => new TenantAdminDto(t.Id, t.Name, t.Slug, t.IsActive, t.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(tenants);
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageTenants))
        .WithName("Admin_Tenants");

        // Activate or deactivate a tenant — a tenant-wide kill switch (enforced in request enrichment). You
        // can't deactivate the tenant you're operating in. Auto-audited as an entity change.
        group.MapPut("/tenants/{tenantId:guid}/active", async (
            Guid tenantId, [FromBody] SetActiveRequest body, PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            if (tenantId == current.TenantId && !body.IsActive)
            {
                return Results.BadRequest("You cannot deactivate the tenant you are operating in.");
            }

            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null)
            {
                return Results.NotFound();
            }

            if (tenant.IsActive != body.IsActive)
            {
                tenant.IsActive = body.IsActive;
                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageTenants))
        .WithName("Admin_SetTenantActive");
    }

    // ── Per-tenant module management ─────────────────────────────────────────

    private static void MapModules(RouteGroupBuilder group)
    {
        // Installed modules and whether each is enabled for the caller's tenant (default-on).
        group.MapGet("/modules", async (IModuleCatalog catalog, ITenantModuleStore moduleStore, CancellationToken ct) =>
        {
            var disabled = await moduleStore.GetDisabledModuleIdsAsync(ct);
            var modules = catalog.Manifests
                .Select(m => new ModuleAdminDto(m.Id, m.DisplayName, m.Description, !disabled.Contains(m.Id)))
                .OrderBy(m => m.DisplayName, StringComparer.Ordinal)
                .ToArray();
            return Results.Ok(modules);
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageModules))
        .WithName("Admin_Modules");

        // Enable or disable an installed module for the caller's tenant. The change is auto-audited as an
        // entity change by the AuditInterceptor; enforcement is in GET /api/platform/modules.
        group.MapPut("/modules/{moduleId}", async (
            string moduleId, [FromBody] ModuleToggleRequest body,
            IModuleCatalog catalog, PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            if (!catalog.Manifests.Any(m => string.Equals(m.Id, moduleId, StringComparison.Ordinal)))
            {
                return Results.NotFound();
            }
            if (current.TenantId is not Guid tenantId)
            {
                return Results.BadRequest("No tenant context.");
            }

            var row = await db.TenantModules.FirstOrDefaultAsync(tm => tm.ModuleId == moduleId, ct);
            if (row is null)
            {
                db.TenantModules.Add(new TenantModule { TenantId = tenantId, ModuleId = moduleId, IsEnabled = body.Enabled });
            }
            else
            {
                row.IsEnabled = body.Enabled;
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageModules))
        .WithName("Admin_SetModuleEnabled");
    }

    // ── Security map + role model ────────────────────────────────────────────

    private static void MapSecurity(RouteGroupBuilder group)
    {
        // The complete, inspectable permission map: platform permissions + every module tool +
        // every installed connector's tools (grantable regardless of per-tenant enablement — a
        // grant only matters once an admin also enables the connector).
        group.MapGet("/security/catalog", (
            IModuleCatalog catalog, Cortex.Application.Connectors.IConnectorCatalog connectors) =>
        {
            var platform = PermissionCatalog.Platform
                .Select(p => new PermissionDto(p.Permission, p.Category, p.Description, false, false))
                .ToList();

            var moduleTools = catalog.Manifests
                .Select(m => new ModuleSecurityDto(
                    m.Id,
                    m.DisplayName,
                    m.Tools.Select(t => new PermissionDto(
                        t.Permission,
                        $"Tool · {m.DisplayName}",
                        t.Description,
                        t.RequiresApproval,
                        t.Audit)).ToArray()))
                .Concat(connectors.Manifests.Select(c => new ModuleSecurityDto(
                    $"connectors.{c.Id}",
                    $"{c.DisplayName} (connector)",
                    c.Tools.Select(t => new PermissionDto(
                        t.Permission,
                        $"Tool · {c.DisplayName}",
                        t.Description,
                        t.RequiresApproval,
                        t.Audit)).ToArray())))
                .ToArray();

            return Results.Ok(new SecurityCatalogDto(platform, moduleTools));
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ViewAuditLog))
        .WithName("Admin_SecurityCatalog");

        // Roles and the baseline permissions each grants — the tenant's configured mapping (editable),
        // falling back to the built-in defaults for a tenant that has none yet.
        group.MapGet("/roles", async (PlatformDbContext db, CancellationToken ct) =>
        {
            var rows = await db.RolePermissions
                .Select(r => new { r.Role, r.Permission })
                .ToListAsync(ct);
            var configured = rows
                .GroupBy(r => r.Role, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Permission).ToArray(), StringComparer.Ordinal);
            var tenantHasConfiguration = rows.Count > 0;

            // Built-in roles first (always present), then any custom roles the tenant has defined (which
            // exist purely as their permission rows).
            var customRoles = configured.Keys
                .Where(r => !Roles.All.Contains(r, StringComparer.Ordinal))
                .OrderBy(r => r, StringComparer.Ordinal);

            var roles = Roles.All.Concat(customRoles).Select(role =>
            {
                var builtIn = Roles.All.Contains(role, StringComparer.Ordinal);

                // system_admin is fixed at the global wildcard and not editable (lockout guardrail).
                if (string.Equals(role, Roles.SystemAdmin, StringComparison.Ordinal))
                {
                    return new RoleDto(role, ["*"], Editable: false, BuiltIn: true);
                }

                // A built-in role falls back to its code default until the tenant has any configuration;
                // a custom role only ever exists as its rows.
                var permissions = builtIn && !tenantHasConfiguration
                    ? RolePermissions.ForRole(role).ToArray()
                    : configured.GetValueOrDefault(role, []);

                return new RoleDto(
                    role,
                    permissions.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
                    Editable: true,
                    BuiltIn: builtIn);
            }).ToArray();

            return Results.Ok(roles);
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageRoles))
        .WithName("Admin_Roles");

        // Create a custom (tenant-defined) role with at least one permission.
        group.MapPost("/roles", async (
            [FromBody] CreateRoleRequest body, PlatformDbContext db, ICurrentUser current, IAuditLog auditLog,
            IModuleCatalog catalog, CancellationToken ct) =>
        {
            var role = body.Role?.Trim() ?? string.Empty;
            if (!IsValidCustomRoleName(role))
            {
                return Results.BadRequest("Role name must be 2–64 chars: a lowercase letter, then lowercase letters, digits, or underscores.");
            }
            if (Roles.All.Contains(role, StringComparer.Ordinal))
            {
                return Results.BadRequest($"'{role}' is a built-in role.");
            }
            if (current.TenantId is not Guid tenantId)
            {
                return Results.BadRequest("No tenant context.");
            }

            var permissions = (body.Permissions ?? [])
                .Select(p => p?.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (permissions.Count == 0)
            {
                return Results.BadRequest("A custom role must grant at least one permission.");
            }

            var unknown = PermissionGrantValidator.FindUnknownGrants(permissions, KnownPermissions(catalog));
            if (unknown.Count > 0)
            {
                return Results.BadRequest($"Unknown permission(s): {string.Join(", ", unknown)}. Grant only permissions from the security catalog.");
            }

            var forbidden = PermissionGrantValidator.FindForbiddenGrants(permissions, Permissions.OperatorOnly, current.HasPermission);
            if (forbidden.Count > 0)
            {
                return Results.BadRequest($"Permission(s) reserved for the platform operator: {string.Join(", ", forbidden)}. These grant cross-tenant access and can't be assigned here.");
            }

            // Seed the tenant's built-in defaults first so the configured-vs-default semantics stay consistent.
            await DatabaseInitializer.EnsureRolePermissionsSeededAsync(db, tenantId, ct);
            if (await db.RolePermissions.AnyAsync(r => r.Role == role, ct))
            {
                return Results.Conflict($"Role '{role}' already exists.");
            }

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission { TenantId = tenantId, Role = role, Permission = permission! });
            }
            await db.SaveChangesAsync(ct);

            await auditLog.RecordAuthEventAsync(new AuthAuditEntry
            {
                TenantId = tenantId,
                UserId = current.UserId,
                Subject = current.Subject,
                UserDisplay = current.DisplayName,
                EventType = AuthAuditEventType.RoleCreated,
                Detail = TruncateDetail($"created role '{role}' granting {string.Join(", ", permissions)}"),
            }, ct);

            return Results.Created($"/api/admin/roles/{role}", null);
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageRoles))
        .WithName("Admin_CreateRole");

        // Delete a custom role: removes its permission rows and any assignments of it. Built-in roles can't
        // be deleted.
        group.MapDelete("/roles/{role}", async (
            string role, PlatformDbContext db, ICurrentUser current, IAuditLog auditLog, CancellationToken ct) =>
        {
            if (Roles.All.Contains(role, StringComparer.Ordinal))
            {
                return Results.BadRequest($"'{role}' is a built-in role and cannot be deleted.");
            }

            var permissionRows = await db.RolePermissions.Where(r => r.Role == role).ToListAsync(ct);
            if (permissionRows.Count == 0)
            {
                return Results.NotFound();
            }

            db.RolePermissions.RemoveRange(permissionRows);
            var assignments = await db.UserRoles.Where(ur => ur.Role == role).ToListAsync(ct);
            db.UserRoles.RemoveRange(assignments);
            await db.SaveChangesAsync(ct);

            await auditLog.RecordAuthEventAsync(new AuthAuditEntry
            {
                TenantId = current.TenantId,
                UserId = current.UserId,
                Subject = current.Subject,
                UserDisplay = current.DisplayName,
                EventType = AuthAuditEventType.RoleDeleted,
                Detail = TruncateDetail($"deleted role '{role}' ({assignments.Count} assignment(s) removed)"),
            }, ct);

            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageRoles))
        .WithName("Admin_DeleteRole");

        // Replace the permissions a role grants in the caller's tenant. This is the configurable RBAC
        // baseline: a tenant admin tunes what each role means without a code change. system_admin is
        // rejected (it always holds "*").
        group.MapPut("/roles/{role}/permissions", async (
            string role, [FromBody] RolePermissionsRequest body,
            PlatformDbContext db, ICurrentUser current, IAuditLog auditLog, IModuleCatalog catalog, CancellationToken ct) =>
        {
            if (string.Equals(role, Roles.SystemAdmin, StringComparison.Ordinal))
            {
                return Results.BadRequest("The system_admin role is not editable; it always holds the global wildcard.");
            }
            if (current.TenantId is not Guid tenantId)
            {
                return Results.BadRequest("No tenant context.");
            }

            // The role must be known: a built-in, or a custom role the tenant has already defined.
            var builtIn = Roles.All.Contains(role, StringComparer.Ordinal);
            if (!builtIn && !await db.RolePermissions.AnyAsync(r => r.Role == role, ct))
            {
                return Results.BadRequest($"Unknown role '{role}'. Create it first via POST /api/admin/roles.");
            }

            var desired = (body.Permissions ?? [])
                .Select(p => p?.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // A custom role is defined by its permissions; emptying it would orphan it — require DELETE instead.
            if (!builtIn && desired.Count == 0)
            {
                return Results.BadRequest("A custom role must keep at least one permission; delete the role instead.");
            }

            var unknown = PermissionGrantValidator.FindUnknownGrants(desired, KnownPermissions(catalog));
            if (unknown.Count > 0)
            {
                return Results.BadRequest($"Unknown permission(s): {string.Join(", ", unknown)}. Grant only permissions from the security catalog.");
            }

            var forbidden = PermissionGrantValidator.FindForbiddenGrants(desired, Permissions.OperatorOnly, current.HasPermission);
            if (forbidden.Count > 0)
            {
                return Results.BadRequest($"Permission(s) reserved for the platform operator: {string.Join(", ", forbidden)}. These grant cross-tenant access and can't be assigned here.");
            }

            // Seed the tenant's full default set first if it has none, so replacing one role can't leave the
            // others resolving to empty.
            await DatabaseInitializer.EnsureRolePermissionsSeededAsync(db, tenantId, ct);

            var existing = await db.RolePermissions.Where(r => r.Role == role).ToListAsync(ct);
            var previous = existing.Select(r => r.Permission).ToHashSet(StringComparer.Ordinal);
            db.RolePermissions.RemoveRange(existing);

            foreach (var permission in desired)
            {
                db.RolePermissions.Add(new RolePermission { TenantId = tenantId, Role = role, Permission = permission! });
            }

            await db.SaveChangesAsync(ct);

            // Audit the security-config change (who, which role, and the exact diff) — a change to what a role
            // grants affects every holder, so it belongs in the append-only audit trail like any tool call.
            var added = desired.Where(p => !previous.Contains(p!)).ToArray();
            var removed = previous.Where(p => !desired.Contains(p)).ToArray();
            if (added.Length > 0 || removed.Length > 0)
            {
                await auditLog.RecordAuthEventAsync(new AuthAuditEntry
                {
                    TenantId = tenantId,
                    UserId = current.UserId,
                    Subject = current.Subject,
                    UserDisplay = current.DisplayName,
                    EventType = AuthAuditEventType.RolePermissionsChanged,
                    Detail = DescribeRoleChange(role, added!, removed),
                }, ct);
            }

            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageRoles))
        .WithName("Admin_SetRolePermissions");
    }

    // ── User RBAC management ─────────────────────────────────────────────────

    private static void MapUsers(RouteGroupBuilder group)
    {
        // Users in the caller's tenant (query filter scopes this automatically), with roles + grants.
        group.MapGet("/users", async (PlatformDbContext db, CancellationToken ct) =>
        {
            // Materialize first; the ordinal sorting below can't be translated to SQL.
            var rows = await db.Users
                .Include(u => u.Roles)
                .Include(u => u.Permissions)
                .OrderBy(u => u.Email)
                .ToListAsync(ct);

            var users = rows.Select(u => new UserDto(
                u.Id,
                u.Subject,
                u.Email,
                u.DisplayName,
                u.IsActive,
                u.LastSeenAt,
                u.Roles.Select(r => r.Role).OrderBy(r => r, StringComparer.Ordinal).ToArray(),
                u.Permissions.Select(p => p.Permission).OrderBy(p => p, StringComparer.Ordinal).ToArray()))
                .ToList();
            return Results.Ok(users);
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageUsers))
        .WithName("Admin_Users");

        // Activate or deactivate a user. A deactivated user keeps a valid token but is denied every request
        // (enforced in the request-enrichment middleware + hub filter). The change is auto-audited as an
        // entity change by the AuditInterceptor.
        group.MapPut("/users/{userId:guid}/active", async (
            Guid userId, [FromBody] SetActiveRequest body, PlatformDbContext db, ICurrentUser current, CancellationToken ct) =>
        {
            if (userId == current.UserId && !body.IsActive)
            {
                return Results.BadRequest("You cannot deactivate your own account.");
            }

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (user.IsActive != body.IsActive)
            {
                user.IsActive = body.IsActive;
                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageUsers))
        .WithName("Admin_SetUserActive");

        // Assign a role to a user.
        group.MapPost("/users/{userId:guid}/roles", async (
            Guid userId, [FromBody] RoleChangeRequest body,
            PlatformDbContext db, ICurrentUser current, IAuditLog auditLog, CancellationToken ct) =>
        {
            if (!Roles.All.Contains(body.Role, StringComparer.Ordinal)
                && !await db.RolePermissions.AnyAsync(r => r.Role == body.Role, ct))
            {
                return Results.BadRequest($"Unknown role '{body.Role}'.");
            }

            // Escalation guard: you cannot assign a role that holds an operator-reserved permission you lack —
            // e.g. a tenant admin must not hand out system_admin (its global wildcard is cross-tenant control).
            // The role's effective grants are the tenant's configured rows if any, else the built-in default.
            var roleGrants = await db.RolePermissions.Where(r => r.Role == body.Role).Select(r => r.Permission).ToListAsync(ct);
            if (roleGrants.Count == 0)
            {
                roleGrants = RolePermissions.ForRole(body.Role).ToList();
            }
            var forbidden = PermissionGrantValidator.FindForbiddenGrants(roleGrants, Permissions.OperatorOnly, current.HasPermission);
            if (forbidden.Count > 0)
            {
                return Results.BadRequest(
                    $"The '{body.Role}' role holds operator-reserved permission(s) ({string.Join(", ", forbidden)}) granting cross-tenant access; you cannot assign it.");
            }

            var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (!user.Roles.Any(r => string.Equals(r.Role, body.Role, StringComparison.Ordinal)))
            {
                user.Roles.Add(new UserRole { TenantId = current.TenantId ?? user.TenantId, UserId = user.Id, Role = body.Role });
                await db.SaveChangesAsync(ct);
                await AuditUserChangeAsync(auditLog, current, user, AuthAuditEventType.RoleAssigned,
                    $"assigned role '{body.Role}' to {user.Email}", ct);
            }

            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageRoles))
        .AddEndpointFilter<RequiresDatabaseAuthorizationFilter>()
        .WithName("Admin_AssignRole");

        // Revoke a role from a user.
        group.MapDelete("/users/{userId:guid}/roles/{role}", async (
            Guid userId, string role, PlatformDbContext db, ICurrentUser current, IAuditLog auditLog, CancellationToken ct) =>
        {
            var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            var assignment = user.Roles.FirstOrDefault(r => string.Equals(r.Role, role, StringComparison.Ordinal));
            if (assignment is not null)
            {
                user.Roles.Remove(assignment);
                await db.SaveChangesAsync(ct);
                await AuditUserChangeAsync(auditLog, current, user, AuthAuditEventType.RoleRevoked,
                    $"revoked role '{role}' from {user.Email}", ct);
            }

            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageRoles))
        .AddEndpointFilter<RequiresDatabaseAuthorizationFilter>()
        .WithName("Admin_RevokeRole");

        // Grant a fine-grained permission (e.g. a single tool, or a module wildcard).
        group.MapPost("/users/{userId:guid}/permissions", async (
            Guid userId, [FromBody] PermissionChangeRequest body,
            PlatformDbContext db, ICurrentUser current, IAuditLog auditLog, IModuleCatalog catalog, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Permission))
            {
                return Results.BadRequest("Permission is required.");
            }

            var permission = body.Permission.Trim();
            if (PermissionGrantValidator.FindUnknownGrants([permission], KnownPermissions(catalog)).Count > 0)
            {
                return Results.BadRequest($"Unknown permission '{permission}'. Grant only permissions from the security catalog.");
            }
            if (PermissionGrantValidator.FindForbiddenGrants([permission], Permissions.OperatorOnly, current.HasPermission).Count > 0)
            {
                return Results.BadRequest($"'{permission}' is reserved for the platform operator and grants cross-tenant access; it can't be assigned here.");
            }

            var user = await db.Users.Include(u => u.Permissions).FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (!user.Permissions.Any(p => string.Equals(p.Permission, permission, StringComparison.Ordinal)))
            {
                user.Permissions.Add(new UserPermission
                {
                    TenantId = current.TenantId ?? user.TenantId,
                    UserId = user.Id,
                    Permission = permission,
                });
                await db.SaveChangesAsync(ct);
                await AuditUserChangeAsync(auditLog, current, user, AuthAuditEventType.PermissionGranted,
                    $"granted '{permission}' to {user.Email}", ct);
            }

            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageRoles))
        .AddEndpointFilter<RequiresDatabaseAuthorizationFilter>()
        .WithName("Admin_GrantPermission");

        // Revoke a fine-grained permission. Permission strings contain dots/wildcards, so take it in the body.
        group.MapPost("/users/{userId:guid}/permissions/revoke", async (
            Guid userId, [FromBody] PermissionChangeRequest body,
            PlatformDbContext db, ICurrentUser current, IAuditLog auditLog, CancellationToken ct) =>
        {
            var user = await db.Users.Include(u => u.Permissions).FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            var grant = user.Permissions.FirstOrDefault(p => string.Equals(p.Permission, body.Permission, StringComparison.Ordinal));
            if (grant is not null)
            {
                user.Permissions.Remove(grant);
                await db.SaveChangesAsync(ct);
                await AuditUserChangeAsync(auditLog, current, user, AuthAuditEventType.PermissionRevoked,
                    $"revoked '{grant.Permission}' from {user.Email}", ct);
            }

            return Results.NoContent();
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageRoles))
        .AddEndpointFilter<RequiresDatabaseAuthorizationFilter>()
        .WithName("Admin_RevokePermission");
    }

    // ── Audit + token usage ──────────────────────────────────────────────────

    private static void MapAuditAndUsage(RouteGroupBuilder group)
    {
        // Recent tool-call audit entries for the tenant (the audit store has no query filter — scope explicitly).
        group.MapGet("/audit/tool-calls", async (
            AuditDbContext audit, ICurrentUser current, int? take, CancellationToken ct) =>
        {
            var tenantId = current.TenantId ?? Guid.Empty;
            var entries = await audit.ToolCalls
                .Where(t => t.TenantId == tenantId)
                .OrderByDescending(t => t.OccurredAt)
                .Take(Math.Clamp(take ?? 100, 1, 500))
                .Select(t => new ToolCallDto(
                    t.Id, t.OccurredAt, t.UserDisplay, t.ModuleId, t.ToolName, t.Permission, t.Success, t.Error, t.DurationMs))
                .ToListAsync(ct);
            return Results.Ok(entries);
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ViewAuditLog))
        .WithName("Admin_AuditToolCalls");

        // Recent identity / authorization events for the tenant: sign-ins, user provisioning, permission
        // grants/revokes, role-baseline changes, and access denials — the security half of the audit trail.
        group.MapGet("/audit/auth-events", async (
            AuditDbContext audit, ICurrentUser current, int? take, CancellationToken ct) =>
        {
            var tenantId = current.TenantId ?? Guid.Empty;
            var entries = await audit.AuthEvents
                .Where(e => e.TenantId == tenantId)
                .OrderByDescending(e => e.OccurredAt)
                .Take(Math.Clamp(take ?? 100, 1, 500))
                .Select(e => new AuthEventDto(
                    e.Id, e.OccurredAt, e.EventType, e.UserDisplay, e.Subject, e.Detail, e.IpAddress))
                .ToListAsync(ct);
            return Results.Ok(entries);
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ViewAuditLog))
        .WithName("Admin_AuditAuthEvents");

        // Token usage aggregated by module over a recent window, plus a daily series.
        group.MapGet("/usage", async (
            AuditDbContext audit, ICurrentUser current, int? days, CancellationToken ct) =>
        {
            var tenantId = current.TenantId ?? Guid.Empty;
            var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days ?? 30, 1, 365));

            var rows = await audit.TokenUsage
                .Where(u => u.TenantId == tenantId && u.OccurredAt >= since)
                .ToListAsync(ct);

            var byModule = rows
                .GroupBy(u => u.ModuleId)
                .Select(g => new UsageByModuleDto(
                    g.Key,
                    g.Sum(x => x.InputTokens),
                    g.Sum(x => x.OutputTokens),
                    g.Sum(x => x.TotalTokens),
                    g.Count()))
                .OrderByDescending(x => x.TotalTokens)
                .ToArray();

            var byDay = rows
                .GroupBy(u => u.OccurredAt.UtcDateTime.Date)
                .Select(g => new UsageByDayDto(
                    DateOnly.FromDateTime(g.Key),
                    g.Sum(x => x.TotalTokens)))
                .OrderBy(x => x.Day)
                .ToArray();

            return Results.Ok(new UsageReportDto(
                rows.Sum(x => x.InputTokens),
                rows.Sum(x => x.OutputTokens),
                rows.Sum(x => x.TotalTokens),
                rows.Count,
                byModule,
                byDay));
        })
        .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ViewAuditLog))
        .WithName("Admin_Usage");
    }

    /// <summary>Records an auth-audit event for a security change one user made to another (attributed to the
    /// actor, with the target and the exact change in <c>Detail</c>). Used by the role/permission endpoints.</summary>
    private static Task AuditUserChangeAsync(
        IAuditLog auditLog, ICurrentUser actor, User target, AuthAuditEventType eventType, string detail, CancellationToken ct) =>
        auditLog.RecordAuthEventAsync(new AuthAuditEntry
        {
            TenantId = actor.TenantId ?? target.TenantId,
            UserId = actor.UserId,
            Subject = actor.Subject,
            UserDisplay = actor.DisplayName,
            EventType = eventType,
            Detail = detail.Length <= 1000 ? detail : detail[..1000],
        }, ct);

    /// <summary>Formats a role-permission diff for the audit Detail field, bounded to its 1000-char column.</summary>
    private static string DescribeRoleChange(string role, string[] added, string[] removed)
    {
        var parts = new List<string>(2);
        if (added.Length > 0)
        {
            parts.Add("granted " + string.Join(", ", added));
        }
        if (removed.Length > 0)
        {
            parts.Add("revoked " + string.Join(", ", removed));
        }

        return TruncateDetail($"role '{role}': {string.Join("; ", parts)}");
    }

    /// <summary>Caps an audit Detail string to its 1000-char column.</summary>
    private static string TruncateDetail(string detail) => detail.Length <= 1000 ? detail : detail[..1000];

    /// <summary>The permissions this deployment recognises — platform permissions plus every registered module tool.</summary>
    private static IReadOnlySet<string> KnownPermissions(IModuleCatalog catalog) =>
        PermissionGrantValidator.KnownPermissions(catalog.Manifests.SelectMany(m => m.Tools).Select(t => t.Permission));

    /// <summary>
    /// Validates a custom role name: 2–64 chars, a lowercase letter followed by lowercase letters, digits,
    /// or underscores. Kept deliberately conservative so role names are safe in URLs, claims, and the UI.
    /// </summary>
    private static bool IsValidCustomRoleName(string role)
    {
        if (role.Length is < 2 or > 64 || role[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var c in role)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed record SecurityCatalogDto(IReadOnlyList<PermissionDto> Platform, IReadOnlyList<ModuleSecurityDto> Modules);
    private sealed record ModuleSecurityDto(string Id, string DisplayName, PermissionDto[] Tools);
    private sealed record PermissionDto(string Permission, string Category, string Description, bool RequiresApproval, bool Audited);
    private sealed record RoleDto(string Role, string[] Permissions, bool Editable, bool BuiltIn);

    private sealed record UserDto(
        Guid Id, string Subject, string Email, string? DisplayName, bool IsActive, DateTimeOffset? LastSeenAt, string[] Roles, string[] Permissions);

    private sealed record SetActiveRequest(bool IsActive);
    private sealed record RoleChangeRequest(string Role);
    private sealed record PermissionChangeRequest(string Permission);
    private sealed record RolePermissionsRequest(string[]? Permissions);
    private sealed record CreateRoleRequest(string? Role, string[]? Permissions);

    private sealed record ModuleAdminDto(string Id, string DisplayName, string? Description, bool Enabled);
    private sealed record ModuleToggleRequest(bool Enabled);

    private sealed record TenantAdminDto(Guid Id, string Name, string Slug, bool IsActive, DateTimeOffset CreatedAt);

    private sealed record AiSettingsDto(
        string? SystemPromptOverride, int? MaxConversationTokensOverride, long? MaxMonthlyTokensOverride,
        string DefaultSystemPrompt, int DefaultMaxConversationTokens, long DefaultMaxMonthlyTokens);
    private sealed record AiSettingsRequest(string? SystemPrompt, int? MaxConversationTokens, long? MaxMonthlyTokens);

    private sealed record AgentProfileDto(Guid Id, string ModuleId, string Name, string Instructions, string Mode, bool IsDefault);

    /// <summary>Create or update a named agent profile for a module (matched by moduleId + name).</summary>
    private sealed record AgentProfileRequest(string? ModuleId, string? Name, string? Instructions, string? Mode, bool IsDefault);

    private sealed record InstructionSnapshotDto(string Hash, string Instructions, DateTimeOffset FirstSeenAt);

    private sealed record NotificationSettingsDto(string? WebhookUrl, bool HasWebhookSecret);

    /// <summary>Update notification delivery. WebhookSecret: null = keep, "" = clear, value = replace.</summary>
    private sealed record NotificationSettingsRequest(string? WebhookUrl, string? WebhookSecret);

    private sealed record ToolCallDto(
        Guid Id, DateTimeOffset OccurredAt, string? UserDisplay, string ModuleId, string ToolName, string Permission, bool Success, string? Error, long DurationMs);

    private sealed record AuthEventDto(
        Guid Id, DateTimeOffset OccurredAt, AuthAuditEventType EventType, string? UserDisplay, string? Subject, string? Detail, string? IpAddress);

    private sealed record UsageReportDto(
        long InputTokens, long OutputTokens, long TotalTokens, int Turns, UsageByModuleDto[] ByModule, UsageByDayDto[] ByDay);
    private sealed record UsageByModuleDto(string ModuleId, long InputTokens, long OutputTokens, long TotalTokens, int Turns);
    private sealed record UsageByDayDto(DateOnly Day, long TotalTokens);
}

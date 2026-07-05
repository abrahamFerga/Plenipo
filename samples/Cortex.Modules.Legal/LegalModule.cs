using Cortex.Application.Authorization;
using Cortex.Modules.Legal.Persistence;
using Cortex.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Modules.Legal;

/// <summary>
/// The Legal vertical — a matter-centric legal assistant modeled on the market's table stakes
/// (see research/legal-ai.md and docs/LEGAL_VERTICAL_PLAN.md): matters as engagement workspaces,
/// documents attached to matters via the platform file store, a clause library, and drafting.
/// The same SDK, RBAC, audit, HITL approvals, token-usage, and chat channels apply with no
/// platform changes.
/// </summary>
public sealed class LegalModule : IModule
{
    public const string Id = "legal";

    public const string ViewClauses = "legal.clauses.view";
    public const string ViewMatters = "legal.matters.view";

    /// <summary>Curate the firm's clause library and playbook (add/remove entries).</summary>
    public const string ManageLibrary = "legal.library.manage";

    public ModuleManifest Manifest { get; } = new()
    {
        Id = Id,
        DisplayName = "Legal",
        Version = "1.12.0",
        Description = "Matter-centric legal assistant. Organize case documents into matters, docket deadlines with reminders, run conflict checks at intake, track billable time, manage matter tasks, search a clause library, and draft clauses for review.",
        Icon = "scale",
        AgentInstructions =
            "You are Cortex's legal assistant, organized around MATTERS (engagement workspaces). " +
            "When the user references a case or client engagement, work within that matter: use list_matters / " +
            "create_matter to resolve it, attach_document_to_matter to file documents the user sends (the message " +
            "carries a '[Attached files]' block with file ids), and list_matter_documents to see a matter's files. " +
            "To answer questions about a matter's documents: when the matter is indexed, PREFER search_knowledge " +
            "(scope it with collection 'matter: <name>') — it returns quoted passages with citations; offer " +
            "index_matter_documents when it is not indexed yet. For a single specific file, call read_document with " +
            "its file id. Either way, CITE the file name and id for every claim you take from a document — never " +
            "state document contents without a citation. " +
            "Use search_clauses / draft_clause for clause work (the firm's own curated library). To deliver a draft as " +
            "work product, chain the tools: draft_clause, then generate_pdf with the drafted text, then " +
            "attach_document_to_matter with the returned file id. When reviewing a contract, first call get_playbook " +
            "and check the document against every rule, citing the file for each finding. " +
            "DOCKETING: when the user mentions a court date, filing deadline, or limitation period, offer to docket " +
            "it with add_deadline (matter, title, ISO due date); use list_deadlines to answer 'what's coming up' and " +
            "complete_deadline when something is done. Deadlines drive reminder notifications — never rely on memory. " +
            "INTAKE: before creating a matter for a new client or engagement, run check_conflicts on the client and " +
            "every known opposing party, and report the result before proceeding; record parties with add_party " +
            "(client / adverse / related) as they emerge — the conflict check is only as good as the recorded parties. " +
            "TIME: when the user mentions work done ('spent an hour on', 'log 0.5h'), capture it immediately with " +
            "log_time (matter, hours, narrative description); answer 'what did I work on' with list_time. " +
            "BILLING: when asked to prepare a bill or pre-bill, use export_prebill (matter, optional date range) — " +
            "it files the PDF on the matter for billing review. " +
            "TASKS: track to-dos with add_task (matter, title, optional assignee and target date), list_tasks, and " +
            "complete_task; use tasks for work items and add_deadline for hard dates that must remind. " +
            "STATUS UPDATES: when the client should hear where things stand, use draft_status_update — it composes " +
            "a client-facing letter (progress, upcoming dates, hours; never internal notes or strategy) and files it " +
            "as a DRAFT for attorney review; never present it as sent. " +
            "CLOSE-OUT: close a finished matter with close_matter — it refuses while deadlines or tasks are open; " +
            "resolve them first, and only pass force after the user explicitly confirms leaving items open. " +
            "BRIEFING: answer 'brief me on X' / 'where does X stand' with get_matter_overview — the one-look " +
            "status of a matter's parties, deadlines, tasks, time, and documents; prefer it before working a matter. " +
            "INTAKE WORKFLOW (onboarding a new client, step by step, confirming each step): " +
            "(1) check_conflicts on the client and every known opposing party and REPORT the result — stop if not clear; " +
            "(2) create_matter with a descriptive name and the client; " +
            "(3) add_party for the client (role client) and each opposing party (role adverse) so future conflict checks see them; " +
            "(4) draft_clause 'engagement letter' with the firm and the client as the parties; " +
            "(5) generate_pdf with the letter text, then attach_document_to_matter with the returned file id. " +
            "Always make clear that " +
            "output is a starting template, not legal advice, and recommend review by a licensed attorney. Never " +
            "invent statutes, case citations, or jurisdiction-specific rules; if asked for those, say a qualified " +
            "lawyer must confirm them.",
        // Guided workflows (v1 item 8): each starter drives a packaged multi-step chain the
        // instructions prescribe — review-against-playbook, draft-and-file, matter Q&A with citations.
        SuggestedPrompts =
        [
            "List my matters",
            "What deadlines are coming up?",
            "Brief me on a matter — parties, deadlines, tasks, time, documents",
            "Run a conflict check for a prospective client",
            "Onboard a new client: conflict check, open the matter, and prepare the engagement letter",
            "Review the attached contract against our playbook and file the memo on the matter",
            "Draft an NDA between our client and the counterparty, and file it on the matter",
            "Summarize the documents on a matter, citing each file",
            "Search the clause library for indemnification",
        ],
        Roles = ["legal:user", "legal:admin"],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "search_clauses",
                Description = "Search the standard clause library by keyword; returns clause titles, categories, and summaries.",
                Permission = Permissions.ForTool(Id, "search_clauses"),
            },
            new ToolDescriptor
            {
                Name = "draft_clause",
                Description = "Draft a standard contract clause filled in with the two party names.",
                Permission = Permissions.ForTool(Id, "draft_clause"),
            },
            new ToolDescriptor
            {
                Name = "create_matter",
                Description = "Create a legal matter (engagement workspace). Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "create_matter"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_matters",
                Description = "List the tenant's matters with status and document counts.",
                Permission = Permissions.ForTool(Id, "list_matters"),
            },
            new ToolDescriptor
            {
                Name = "attach_document_to_matter",
                Description = "Attach a stored file to a matter by name. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "attach_document_to_matter"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_matter_documents",
                Description = "List a matter's attached documents with their file ids.",
                Permission = Permissions.ForTool(Id, "list_matter_documents"),
            },
            new ToolDescriptor
            {
                Name = "get_playbook",
                Description = "Get the firm's contract-review playbook rules with severity.",
                Permission = Permissions.ForTool(Id, "get_playbook"),
            },
            new ToolDescriptor
            {
                Name = "start_bulk_review",
                Description = "Start a background bulk review of all documents on a matter against a set of questions. Side-effecting: consumes resources and files a report, requires human approval.",
                Permission = Permissions.ForTool(Id, "start_bulk_review"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "index_matter_documents",
                Description = "Index a matter's documents into its searchable knowledge collection (for search_knowledge). Side-effecting: consumes resources, requires human approval.",
                Permission = Permissions.ForTool(Id, "index_matter_documents"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "add_deadline",
                Description = "Docket a deadline on a matter (court date, filing deadline, limitation period); a reminder fires before it is due. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "add_deadline"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_deadlines",
                Description = "List upcoming deadlines soonest-first, across all matters or for one matter; overdue items are flagged.",
                Permission = Permissions.ForTool(Id, "list_deadlines"),
            },
            new ToolDescriptor
            {
                Name = "complete_deadline",
                Description = "Mark a docketed deadline as completed. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "complete_deadline"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "add_party",
                Description = "Record a party on a matter (client, adverse, or related) — feeds future conflict checks. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "add_party"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_parties",
                Description = "List the parties recorded on a matter with their roles.",
                Permission = Permissions.ForTool(Id, "list_parties"),
            },
            new ToolDescriptor
            {
                Name = "check_conflicts",
                Description = "Conflict-of-interest check: search every recorded party and client across the firm's matters (restricted matters report anonymously). Run before any new engagement.",
                Permission = Permissions.ForTool(Id, "check_conflicts"),
            },
            new ToolDescriptor
            {
                Name = "log_time",
                Description = "Log time worked on a matter (billable by default). Quick capture — appends an entry without an approval step.",
                Permission = Permissions.ForTool(Id, "log_time"),
            },
            new ToolDescriptor
            {
                Name = "list_time",
                Description = "List logged time: a matter's entries with billable totals, or the caller's own recent time across matters.",
                Permission = Permissions.ForTool(Id, "list_time"),
            },
            new ToolDescriptor
            {
                Name = "add_task",
                Description = "Add a task (to-do) on a matter, optionally assigned by name with a target date. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "add_task"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "list_tasks",
                Description = "List open tasks across all matters or for one matter (dated tasks first).",
                Permission = Permissions.ForTool(Id, "list_tasks"),
            },
            new ToolDescriptor
            {
                Name = "complete_task",
                Description = "Mark a matter task as completed. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "complete_task"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "get_matter_overview",
                Description = "One-look matter brief: status, parties, open deadlines, open tasks, time totals, recent documents.",
                Permission = Permissions.ForTool(Id, "get_matter_overview"),
            },
            new ToolDescriptor
            {
                Name = "export_prebill",
                Description = "Generate a pre-bill (time entries + billable totals over a period) as a PDF filed on the matter. Side-effecting: writes a document and requires human approval.",
                Permission = Permissions.ForTool(Id, "export_prebill"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "close_matter",
                Description = "Close a matter after a completeness check (refuses while deadlines/tasks are open unless forced). Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "close_matter"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "reopen_matter",
                Description = "Reopen a closed matter. Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "reopen_matter"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "draft_status_update",
                Description = "Draft a client-facing status letter (recent progress, upcoming dates, hours) filed on the matter for attorney review. Side-effecting: writes a document and requires human approval.",
                Permission = Permissions.ForTool(Id, "draft_status_update"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "restrict_matter_access",
                Description = "Put a matter behind an ethical wall (only the caller keeps access). Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "restrict_matter_access"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "open_matter_access",
                Description = "Lift a matter's ethical wall (caller must be inside it). Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "open_matter_access"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "connect_matter_folder",
                Description = "Bind a matter to a folder in a connected data source and start syncing (files attach + index automatically). Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "connect_matter_folder"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "sync_matter_folder",
                Description = "Re-sync a matter's bound folder (new/changed files attach + index; unchanged skipped). Side-effecting and requires human approval.",
                Permission = Permissions.ForTool(Id, "sync_matter_folder"),
                RequiresApproval = true,
            },
        ],
        Tabs =
        [
            new TabDescriptor { Id = "chat", Label = "Chat", Route = "/legal/chat", Icon = "message-circle", Order = 0 },
            new TabDescriptor
            {
                Id = "matters", Label = "Matters", Route = "/legal/matters", Icon = "folder", Order = 1,
                Permission = ViewMatters,
                DataEndpoint = "/api/legal/matters",
                Columns =
                [
                    new("name", "Matter"), new("clientName", "Client"), new("status", "Status"),
                    new("documentCount", "Documents"), new("createdAt", "Opened"),
                ],
            },
            new TabDescriptor
            {
                Id = "deadlines", Label = "Deadlines", Route = "/legal/deadlines", Icon = "calendar-clock", Order = 2,
                Permission = ViewMatters,
                DataEndpoint = "/api/legal/deadlines",
                Columns =
                [
                    new("dueAt", "Due"), new("title", "Deadline"), new("matterName", "Matter"),
                    new("status", "Status"), new("notes", "Notes"),
                ],
            },
            new TabDescriptor
            {
                Id = "tasks", Label = "Tasks", Route = "/legal/tasks", Icon = "list-checks", Order = 3,
                Permission = ViewMatters,
                DataEndpoint = "/api/legal/tasks",
                Columns =
                [
                    new("dueOn", "Target"), new("title", "Task"), new("matterName", "Matter"),
                    new("assignedTo", "Assigned to"), new("status", "Status"), new("notes", "Notes"),
                ],
            },
            new TabDescriptor
            {
                Id = "time", Label = "Time", Route = "/legal/time", Icon = "timer", Order = 4,
                Permission = ViewMatters,
                DataEndpoint = "/api/legal/time",
                Columns =
                [
                    new("workedOn", "Date"), new("matterName", "Matter"), new("hours", "Hours"),
                    new("description", "Narrative"), new("userDisplay", "By"), new("billable", "Billable"),
                ],
            },
            new TabDescriptor
            {
                Id = "clauses", Label = "Clauses", Route = "/legal/clauses", Icon = "file-text", Order = 5,
                Permission = ViewClauses,
                DataEndpoint = "/api/legal/clauses",
                Columns = [new("title", "Clause"), new("category", "Category"), new("summary", "Summary")],
            },
            new TabDescriptor
            {
                Id = "playbook", Label = "Playbook", Route = "/legal/playbook", Icon = "shield-check", Order = 6,
                Permission = ViewClauses,
                DataEndpoint = "/api/legal/playbook",
                Columns = [new("severity", "Severity"), new("title", "Rule"), new("guidance", "Guidance")],
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<LegalTools>();
        services.AddScoped<MatterTools>();
        services.AddSingleton<IModuleToolSource, LegalToolSource>();
        services.AddSingleton<Cortex.Application.Jobs.IJobHandler, BulkReviewJobHandler>();

        // A matter's RAG collection is gated by the matter itself (wall included) — scope-first
        // retrieval. Registered unconditionally; it only ever runs when RAG is enabled.
        services.AddScoped<Cortex.Application.Rag.IRagCollectionGate, MatterRagGate>();

        // Connector sync lands here: synced files attach to the bound matter and index into its
        // knowledge collection (the connect_matter_folder / sync_matter_folder chain).
        services.AddScoped<Cortex.Application.Connectors.IConnectorSyncHandler, MatterSyncHandler>();

        // Docketing reminders: one notification per deadline as its window opens (inbox + channels).
        services.AddHostedService<DeadlineReminderService>();

        // The module owns its data under the 'legal' schema of the platform database.
        services.AddDbContext<LegalDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString(LegalDbContext.ConnectionName)));
    }

    public async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<LegalDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Seeds the tenant's clause library and playbook from the built-in defaults, so drafting and
    /// review work out of the box. Tenant-owned data: only seeds when an ambient tenant is present
    /// (the dev tenant in Development), and only into an empty library — a firm's curation is never
    /// overwritten.
    /// </summary>
    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tenant = services.GetRequiredService<Cortex.Core.Multitenancy.ITenantContext>();
        if (!tenant.HasTenant)
        {
            return;
        }

        var db = services.GetRequiredService<LegalDbContext>();
        var tenantId = tenant.RequireTenantId();

        if (!await db.Clauses.AnyAsync(cancellationToken))
        {
            foreach (var clause in LegalCatalog.Clauses)
            {
                db.Clauses.Add(new TenantClause
                {
                    TenantId = tenantId,
                    Slug = clause.Id,
                    Title = clause.Title,
                    Category = clause.Category,
                    Summary = clause.Summary,
                    Template = clause.Template,
                });
            }
        }

        if (!await db.PlaybookRules.AnyAsync(cancellationToken))
        {
            foreach (var (title, guidance, severity) in LegalCatalog.DefaultPlaybook)
            {
                db.PlaybookRules.Add(new PlaybookRule
                {
                    TenantId = tenantId,
                    Title = title,
                    Guidance = guidance,
                    Severity = severity,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/legal").WithTags("Legal").RequireAuthorization();

        // The tenant's clause library (seeded from defaults, curated by the firm).
        group.MapGet("/clauses", async (string? query, LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var clauses = await db.Clauses.OrderBy(c => c.Title).Take(500).ToListAsync(cancellationToken);
                var selected = string.IsNullOrWhiteSpace(query)
                    ? clauses
                    : LegalCatalog.Search(clauses, query, c => [c.Title, c.Category, c.Summary, c.Slug]);
                return Results.Ok(selected.Select(c => new ClauseDto(c.Id, c.Slug, c.Title, c.Category, c.Summary)));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewClauses))
            .WithName("Legal_GetClauses");

        // Curate the clause library.
        group.MapPost("/clauses", async (
                UpsertClauseRequest body, LegalDbContext db,
                Cortex.Core.Multitenancy.ITenantContext tenant, CancellationToken cancellationToken) =>
            {
                var existing = await db.Clauses.FirstOrDefaultAsync(c => c.Slug == body.Slug, cancellationToken);
                if (existing is null)
                {
                    existing = new TenantClause
                    {
                        TenantId = tenant.RequireTenantId(),
                        Slug = body.Slug,
                        Title = body.Title,
                        Category = body.Category,
                        Summary = body.Summary,
                        Template = body.Template,
                    };
                    db.Clauses.Add(existing);
                }
                else
                {
                    existing.Title = body.Title;
                    existing.Category = body.Category;
                    existing.Summary = body.Summary;
                    existing.Template = body.Template;
                }

                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new ClauseDto(existing.Id, existing.Slug, existing.Title, existing.Category, existing.Summary));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageLibrary))
            .WithName("Legal_UpsertClause");

        group.MapDelete("/clauses/{slug}", async (string slug, LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var clause = await db.Clauses.FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken);
                if (clause is null)
                {
                    return Results.NotFound();
                }

                db.Clauses.Remove(clause);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageLibrary))
            .WithName("Legal_DeleteClause");

        // The firm playbook — drives the Playbook tab and the get_playbook tool.
        group.MapGet("/playbook", async (LegalDbContext db, CancellationToken cancellationToken) =>
            {
                // Severity persists as a string (readable rows) — order in memory where enum order applies.
                var rules = (await db.PlaybookRules.ToListAsync(cancellationToken))
                    .OrderByDescending(r => r.Severity)
                    .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new PlaybookRuleDto(r.Id, r.Severity.ToString(), r.Title, r.Guidance));
                return Results.Ok(rules);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewClauses))
            .WithName("Legal_GetPlaybook");

        group.MapPost("/playbook", async (
                UpsertPlaybookRuleRequest body, LegalDbContext db,
                Cortex.Core.Multitenancy.ITenantContext tenant, CancellationToken cancellationToken) =>
            {
                var rule = new PlaybookRule
                {
                    TenantId = tenant.RequireTenantId(),
                    Title = body.Title,
                    Guidance = body.Guidance,
                    Severity = body.Severity,
                };
                db.PlaybookRules.Add(rule);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Created($"/api/legal/playbook/{rule.Id}", new PlaybookRuleDto(rule.Id, rule.Severity.ToString(), rule.Title, rule.Guidance));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageLibrary))
            .WithName("Legal_AddPlaybookRule");

        group.MapDelete("/playbook/{id:guid}", async (Guid id, LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var rule = await db.PlaybookRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
                if (rule is null)
                {
                    return Results.NotFound();
                }

                db.PlaybookRules.Remove(rule);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageLibrary))
            .WithName("Legal_DeletePlaybookRule");

        // The tenant's matters — drives the Matters tab (query filter scopes rows to the tenant;
        // the ethical wall filters per user, so a walled matter never renders for outsiders).
        group.MapGet("/matters", async (
                LegalDbContext db, Cortex.Core.Identity.ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var matters = (await db.Matters
                        .OrderByDescending(m => m.CreatedAt)
                        .Take(200)
                        .Select(m => new
                        {
                            m.Id, m.Name, m.ClientName, m.Status, m.RestrictedUserIdsJson,
                            DocumentCount = m.Documents.Count, m.CreatedAt,
                        })
                        .ToListAsync(cancellationToken))
                    .Where(m => Matter.WallAllows(m.RestrictedUserIdsJson, current.UserId))
                    .Select(m => new MatterDto(
                        m.Id, m.Name, m.ClientName, m.Status.ToString(), m.DocumentCount,
                        DateOnly.FromDateTime(m.CreatedAt.UtcDateTime)));
                return Results.Ok(matters);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewMatters))
            .WithName("Legal_GetMatters");

        // Upcoming deadlines across the tenant's matters — drives the Deadlines tab. Open items
        // soonest-first, then recently completed ones; walled matters keep their dates invisible.
        group.MapGet("/deadlines", async (
                LegalDbContext db, Cortex.Core.Identity.ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var now = DateTimeOffset.UtcNow;
                var rows = (await db.MatterDeadlines
                        .OrderBy(d => d.CompletedAt != null) // open first
                        .ThenBy(d => d.DueAt)
                        .Take(200)
                        .Join(db.Matters.Where(m => m.Status == MatterStatus.Open), d => d.MatterId, m => m.Id, (d, m) => new
                        {
                            d.Id, d.Title, d.DueAt, d.Notes, d.CompletedAt,
                            MatterName = m.Name, m.RestrictedUserIdsJson,
                        })
                        .ToListAsync(cancellationToken))
                    .Where(d => Matter.WallAllows(d.RestrictedUserIdsJson, current.UserId))
                    .Select(d => new DeadlineDto(
                        d.Id,
                        DateOnly.FromDateTime(d.DueAt.UtcDateTime),
                        d.Title,
                        d.MatterName,
                        d.CompletedAt is not null ? "Completed" : d.DueAt < now ? "OVERDUE" : d.DueAt <= now.AddDays(7) ? "Due soon" : "Open",
                        d.Notes));
                return Results.Ok(rows);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewMatters))
            .WithName("Legal_GetDeadlines");

        // Open tasks across the tenant's matters (then recently completed) — drives the Tasks tab.
        group.MapGet("/tasks", async (
                LegalDbContext db, Cortex.Core.Identity.ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var rows = (await db.MatterTasks
                        .OrderBy(t => t.CompletedAt != null) // open first
                        .ThenBy(t => t.DueOn == null)        // dated first, soonest first
                        .ThenBy(t => t.DueOn)
                        .ThenBy(t => t.CreatedAt)
                        .Take(200)
                        .Join(db.Matters.Where(m => m.Status == MatterStatus.Open), t => t.MatterId, m => m.Id, (t, m) => new
                        {
                            t.Id, t.Title, t.AssignedTo, t.DueOn, t.Notes, t.CompletedAt,
                            MatterName = m.Name, m.RestrictedUserIdsJson,
                        })
                        .ToListAsync(cancellationToken))
                    .Where(t => Matter.WallAllows(t.RestrictedUserIdsJson, current.UserId))
                    .Select(t => new MatterTaskDto(
                        t.Id, t.DueOn, t.Title, t.MatterName, t.AssignedTo,
                        t.CompletedAt is not null ? "Completed" : "Open", t.Notes));
                return Results.Ok(rows);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewMatters))
            .WithName("Legal_GetTasks");

        // Recent time entries across the tenant's matters — drives the Time tab. Walled matters
        // keep their entries invisible to outsiders, like every other matter surface.
        group.MapGet("/time", async (
                LegalDbContext db, Cortex.Core.Identity.ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var rows = (await db.TimeEntries
                        .OrderByDescending(t => t.WorkedOn)
                        .ThenByDescending(t => t.CreatedAt)
                        .Take(200)
                        .Join(db.Matters, t => t.MatterId, m => m.Id, (t, m) => new
                        {
                            t.Id, t.WorkedOn, t.Hours, t.Description, t.UserDisplay, t.Billable,
                            MatterName = m.Name, m.RestrictedUserIdsJson,
                        })
                        .ToListAsync(cancellationToken))
                    .Where(t => Matter.WallAllows(t.RestrictedUserIdsJson, current.UserId))
                    .Select(t => new TimeEntryDto(
                        t.Id, t.WorkedOn, t.MatterName, t.Hours, t.Description, t.UserDisplay,
                        t.Billable ? "Yes" : "No"));
                return Results.Ok(rows);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewMatters))
            .WithName("Legal_GetTime");

        // A matter's attached documents (file ids resolve against /api/files/{id}). Outside the
        // wall, the matter 404s — indistinguishable from missing, like cross-tenant ids.
        group.MapGet("/matters/{matterId:guid}/documents", async (
                Guid matterId, LegalDbContext db, Cortex.Core.Identity.ICurrentUser current,
                CancellationToken cancellationToken) =>
            {
                var matter = await db.Matters.FirstOrDefaultAsync(m => m.Id == matterId, cancellationToken);
                if (matter is null || !matter.IsAccessibleTo(current.UserId))
                {
                    return Results.NotFound();
                }

                var documents = await db.MatterDocuments
                    .Where(d => d.MatterId == matterId)
                    .OrderByDescending(d => d.CreatedAt)
                    .Select(d => new MatterDocumentDto(d.FileId, d.FileName, d.Note, d.CreatedAt))
                    .ToListAsync(cancellationToken);
                return Results.Ok(documents);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewMatters))
            .WithName("Legal_GetMatterDocuments");
    }

    private sealed record MatterDto(Guid Id, string Name, string? ClientName, string Status, int DocumentCount, DateOnly CreatedAt);

    private sealed record DeadlineDto(Guid Id, DateOnly DueAt, string Title, string MatterName, string Status, string? Notes);

    private sealed record TimeEntryDto(Guid Id, DateOnly WorkedOn, string MatterName, decimal Hours, string Description, string? UserDisplay, string Billable);

    private sealed record MatterTaskDto(Guid Id, DateOnly? DueOn, string Title, string MatterName, string? AssignedTo, string Status, string? Notes);

    private sealed record MatterDocumentDto(Guid FileId, string FileName, string? Note, DateTimeOffset AttachedAt);

    private sealed record ClauseDto(Guid Id, string Slug, string Title, string Category, string Summary);

    /// <summary>Create or update a clause in the firm's library (matched by slug).</summary>
    public sealed record UpsertClauseRequest(string Slug, string Title, string Category, string Summary, string Template);

    private sealed record PlaybookRuleDto(Guid Id, string Severity, string Title, string Guidance);

    /// <summary>Add a rule to the firm's contract-review playbook.</summary>
    public sealed record UpsertPlaybookRuleRequest(string Title, string Guidance, RuleSeverity Severity = RuleSeverity.Caution);
}

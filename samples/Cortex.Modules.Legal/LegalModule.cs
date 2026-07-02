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

    public ModuleManifest Manifest { get; } = new()
    {
        Id = Id,
        DisplayName = "Legal",
        Version = "1.1.0",
        Description = "Matter-centric legal assistant. Organize case documents into matters, search a clause library, and draft clauses for review.",
        Icon = "scale",
        AgentInstructions =
            "You are Cortex's legal assistant, organized around MATTERS (engagement workspaces). " +
            "When the user references a case or client engagement, work within that matter: use list_matters / " +
            "create_matter to resolve it, attach_document_to_matter to file documents the user sends (the message " +
            "carries a '[Attached files]' block with file ids), and list_matter_documents to see a matter's files. " +
            "To answer questions about a matter's documents, call read_document with each file id and CITE the file " +
            "name and id for every claim you take from a document — never state document contents without a citation. " +
            "Use search_clauses / draft_clause for clause work. Always make clear that output is a starting template, " +
            "not legal advice, and recommend review by a licensed attorney. Never invent statutes, case citations, or " +
            "jurisdiction-specific rules; if asked for those, say a qualified lawyer must confirm them.",
        SuggestedPrompts =
        [
            "List my matters",
            "Draft a confidentiality clause",
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
                Id = "clauses", Label = "Clauses", Route = "/legal/clauses", Icon = "file-text", Order = 2,
                Permission = ViewClauses,
                DataEndpoint = "/api/legal/clauses",
                Columns = [new("title", "Clause"), new("category", "Category"), new("summary", "Summary")],
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<LegalTools>();
        services.AddScoped<MatterTools>();
        services.AddSingleton<IModuleToolSource, LegalToolSource>();

        // The module owns its data under the 'legal' schema of the platform database.
        services.AddDbContext<LegalDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString(LegalDbContext.ConnectionName)));
    }

    public async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<LegalDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/legal").WithTags("Legal").RequireAuthorization();

        // The clause library (reference data, not tenant-scoped).
        group.MapGet("/clauses", (string? query) =>
            {
                var clauses = string.IsNullOrWhiteSpace(query)
                    ? LegalCatalog.Clauses
                    : [.. LegalCatalog.Search(query)];
                return Results.Ok(clauses);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewClauses))
            .WithName("Legal_GetClauses");

        // The tenant's matters — drives the Matters tab (query filter scopes rows to the tenant).
        group.MapGet("/matters", async (LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var matters = await db.Matters
                    .OrderByDescending(m => m.CreatedAt)
                    .Take(200)
                    .Select(m => new MatterDto(
                        m.Id, m.Name, m.ClientName, m.Status.ToString(), m.Documents.Count,
                        DateOnly.FromDateTime(m.CreatedAt.UtcDateTime)))
                    .ToListAsync(cancellationToken);
                return Results.Ok(matters);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewMatters))
            .WithName("Legal_GetMatters");

        // A matter's attached documents (file ids resolve against /api/files/{id}).
        group.MapGet("/matters/{matterId:guid}/documents", async (
                Guid matterId, LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var exists = await db.Matters.AnyAsync(m => m.Id == matterId, cancellationToken);
                if (!exists)
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

    private sealed record MatterDocumentDto(Guid FileId, string FileName, string? Note, DateTimeOffset AttachedAt);
}

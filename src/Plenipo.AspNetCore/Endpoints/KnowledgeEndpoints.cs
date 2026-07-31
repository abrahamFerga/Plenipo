using Plenipo.Application.Authorization;
using Plenipo.Application.Jobs;
using Plenipo.Application.Rag;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.AspNetCore.Endpoints;

/// <summary>
/// The knowledge surface: build and curate retrieval collections without writing code. Until this
/// existed, the only way to create a corpus was for a module to call <c>IRagService</c> itself,
/// which made "build your RAG database" an engineering task — the opposite of what a knowledge
/// curator needs.
/// <para>
/// Reading is gated exactly like retrieval (the same collection gates, so this surface can never
/// show a corpus the caller could not search); writing needs
/// <see cref="Permissions.ManageKnowledge"/>. Resource-BOUND collections (a matter's, a property's)
/// are owned by their module and are read-only here — their lifecycle belongs to the resource, and
/// letting an admin delete a matter's corpus from a generic screen would be a surprise.
/// </para>
/// </summary>
public static class KnowledgeEndpoints
{
    /// <summary>Collections created here are unbound and belong to this pseudo-module.</summary>
    public const string CuratedModuleId = "knowledge";

    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/knowledge").WithTags("Knowledge").RequireAuthorization();

        // --- read: gated identically to retrieval ------------------------------------------
        group.MapGet("/", async ([FromServices] IRagService? rag, CancellationToken ct) =>
            {
                if (rag is null)
                {
                    return RagDisabled();
                }

                var collections = await rag.ListCollectionsAsync(ct);
                return Results.Ok(collections.Select(ToDto));
            })
            .WithName("Knowledge_List");

        group.MapGet("/{collectionId:guid}/documents", async (
            Guid collectionId, [FromServices] IRagService? rag, PlatformDbContext db, CancellationToken ct) =>
            {
                if (rag is null)
                {
                    return RagDisabled();
                }

                // Gate first: resolve through the service's own access list rather than querying
                // chunks directly, so this endpoint cannot become a way around a collection gate.
                var allowed = await rag.ListCollectionsAsync(ct);
                if (allowed.All(c => c.Id != collectionId))
                {
                    return Results.NotFound();
                }

                // Grouped into an anonymous type and mapped afterwards: projecting straight into the
                // DTO's positional constructor and then ordering by one of its properties is not
                // something EF can compose over a GroupBy.
                var grouped = await db.RagChunks
                    .Where(c => c.CollectionId == collectionId)
                    .GroupBy(c => new { c.FileId, c.FileName })
                    .Select(g => new
                    {
                        g.Key.FileId,
                        g.Key.FileName,
                        ChunkCount = g.Count(),
                        Language = g.Max(c => c.Language),
                    })
                    .ToListAsync(ct);

                var documents = grouped
                    .Select(d => new KnowledgeDocumentDto(
                        d.FileId, d.FileName, d.ChunkCount, d.Language ?? RagLanguage.Default))
                    .OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Results.Ok(documents);
            })
            .WithName("Knowledge_Documents");

        // A retrieval preview, so a curator can see what the agent will see — the single most
        // useful thing when tuning a corpus, and it runs through the identical code path.
        group.MapPost("/search", async (
            [FromBody] KnowledgeSearchRequest body, [FromServices] IRagService? rag, CancellationToken ct) =>
            {
                if (rag is null)
                {
                    return RagDisabled();
                }

                if (string.IsNullOrWhiteSpace(body.Query))
                {
                    return Results.BadRequest("query is required.");
                }

                var hits = await rag.SearchAsync(body.Query, body.Collection, body.TopK, body.Filters, ct);
                return Results.Ok(hits);
            })
            .WithName("Knowledge_Search");

        // --- write: curator surface ----------------------------------------------------------
        group.MapPost("/", async (
            [FromBody] CreateKnowledgeCollectionRequest body, [FromServices] IRagService? rag, CancellationToken ct) =>
            {
                if (rag is null)
                {
                    return RagDisabled();
                }

                var name = body.Name?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    return Results.BadRequest("name is required.");
                }

                if (body.Language is { Length: > 0 } requested &&
                    !RagLanguage.Supported.Contains(requested.Trim().ToLowerInvariant()))
                {
                    return Results.BadRequest(
                        $"language '{requested}' is not a Postgres text-search configuration. Supported: {string.Join(", ", RagLanguage.Supported.Order(StringComparer.Ordinal))}.");
                }

                var id = await rag.GetOrCreateCollectionAsync(
                    CuratedModuleId, resourceType: null, resourceId: null, name,
                    body.Language, body.Metadata, ct);
                return Results.Ok(new { id });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageKnowledge))
            .WithName("Knowledge_Create");

        group.MapPatch("/{collectionId:guid}", async (
            Guid collectionId, [FromBody] UpdateKnowledgeCollectionRequest body,
            PlatformDbContext db, CancellationToken ct) =>
            {
                var collection = await db.RagCollections.FirstOrDefaultAsync(c => c.Id == collectionId, ct);
                if (collection is null)
                {
                    return Results.NotFound();
                }

                if (collection.ResourceType is not null)
                {
                    return ModuleOwned();
                }

                if (body.Name is { Length: > 0 } name)
                {
                    collection.Name = name.Trim();
                }

                if (body.Language is { Length: > 0 } language)
                {
                    var normalized = RagLanguage.Normalize(language);
                    if (!string.Equals(normalized, language.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        return Results.BadRequest($"language '{language}' is not a Postgres text-search configuration.");
                    }

                    // Only the default for FUTURE documents: already-indexed chunks keep the
                    // configuration their tsv was built with until they are re-indexed.
                    collection.Language = normalized;
                }

                if (body.Metadata is not null)
                {
                    collection.Metadata = new Dictionary<string, string>(body.Metadata, StringComparer.Ordinal);
                }

                await db.SaveChangesAsync(ct);
                return Results.Ok();
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageKnowledge))
            .WithName("Knowledge_Update");

        group.MapDelete("/{collectionId:guid}", async (
            Guid collectionId, PlatformDbContext db, CancellationToken ct) =>
            {
                var collection = await db.RagCollections.FirstOrDefaultAsync(c => c.Id == collectionId, ct);
                if (collection is null)
                {
                    return Results.NotFound();
                }

                if (collection.ResourceType is not null)
                {
                    return ModuleOwned();
                }

                await db.RagChunks.Where(c => c.CollectionId == collectionId).ExecuteDeleteAsync(ct);
                db.RagCollections.Remove(collection);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageKnowledge))
            .WithName("Knowledge_Delete");

        // Index documents already in the tenant file store. Ingestion is a background job for the
        // same reason it always was: extraction and embedding are far too slow for a request.
        group.MapPost("/{collectionId:guid}/documents", async (
            Guid collectionId, [FromBody] IndexDocumentsRequest body,
            PlatformDbContext db, IJobQueue jobs, CancellationToken ct) =>
            {
                if (body.FileIds is not { Count: > 0 })
                {
                    return Results.BadRequest("fileIds is required.");
                }

                var collection = await db.RagCollections.FirstOrDefaultAsync(c => c.Id == collectionId, ct);
                if (collection is null)
                {
                    return Results.NotFound();
                }

                // Tenant-scoped: a file id from another tenant simply is not found here.
                var known = await db.StoredFiles.Where(f => body.FileIds.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct);
                if (known.Count == 0)
                {
                    return Results.BadRequest("None of the supplied fileIds exist in this tenant's file store.");
                }

                var jobId = await jobs.EnqueueAsync(
                    collection.ModuleId,
                    RagIngestJob.Kind,
                    new RagIngestArgs(collectionId, known, body.Principals, body.Metadata, body.Language),
                    ct);

                return Results.Accepted($"/api/jobs/{jobId}", new { jobId, files = known.Count });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageKnowledge))
            .WithName("Knowledge_IndexDocuments");

        group.MapDelete("/{collectionId:guid}/documents/{fileId:guid}", async (
            Guid collectionId, Guid fileId, PlatformDbContext db, CancellationToken ct) =>
            {
                var removed = await db.RagChunks
                    .Where(c => c.CollectionId == collectionId && c.FileId == fileId)
                    .ExecuteDeleteAsync(ct);
                return removed == 0 ? Results.NotFound() : Results.Ok(new { removed });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageKnowledge))
            .WithName("Knowledge_RemoveDocument");

        // Re-index everything in the collection — the operation you need after changing the
        // language, or after an embedding-model change.
        group.MapPost("/{collectionId:guid}/reindex", async (
            Guid collectionId, PlatformDbContext db, IJobQueue jobs, CancellationToken ct) =>
            {
                var collection = await db.RagCollections.FirstOrDefaultAsync(c => c.Id == collectionId, ct);
                if (collection is null)
                {
                    return Results.NotFound();
                }

                var fileIds = await db.RagChunks
                    .Where(c => c.CollectionId == collectionId)
                    .Select(c => c.FileId)
                    .Distinct()
                    .ToListAsync(ct);

                if (fileIds.Count == 0)
                {
                    return Results.BadRequest("The collection has no indexed documents to re-index.");
                }

                var jobId = await jobs.EnqueueAsync(
                    collection.ModuleId, RagIngestJob.Kind, new RagIngestArgs(collectionId, fileIds), ct);
                return Results.Accepted($"/api/jobs/{jobId}", new { jobId, files = fileIds.Count });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ManageKnowledge))
            .WithName("Knowledge_Reindex");

        // The configurations this deployment can index with — drives the language picker rather
        // than hard-coding a list in the UI.
        group.MapGet("/languages", () => Results.Ok(RagLanguage.Supported.Order(StringComparer.Ordinal)))
            .WithName("Knowledge_Languages");
    }

    private static IResult RagDisabled() => Results.Problem(
        title: "Knowledge retrieval is not enabled",
        detail: "This deployment runs with Rag:Enabled = false, so there are no knowledge collections. Enable it in configuration (see docs/CONFIGURATION.md) and restart.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult ModuleOwned() => Results.Problem(
        title: "This collection belongs to a module resource",
        detail: "Resource-bound collections (for example a matter's) are managed through their module, whose tools create and refresh them. Only unbound, curated collections are editable here.",
        statusCode: StatusCodes.Status409Conflict);

    private static KnowledgeCollectionDto ToDto(RagCollectionInfo c) => new(
        c.Id, c.ModuleId, c.ResourceType, c.ResourceId, c.Name, c.Language, c.EmbeddingModel,
        c.DocumentCount, c.ChunkCount, c.Metadata, c.FilterKeys, c.ResourceType is null);

    private sealed record KnowledgeCollectionDto(
        Guid Id, string ModuleId, string? ResourceType, Guid? ResourceId, string Name, string Language,
        string EmbeddingModel, int DocumentCount, int ChunkCount,
        IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<string> FilterKeys, bool IsEditable);

    private sealed record KnowledgeDocumentDto(Guid FileId, string FileName, int ChunkCount, string Language);

    private sealed record CreateKnowledgeCollectionRequest(
        string? Name, string? Language, Dictionary<string, string>? Metadata);

    private sealed record UpdateKnowledgeCollectionRequest(
        string? Name, string? Language, Dictionary<string, string>? Metadata);

    private sealed record IndexDocumentsRequest(
        List<Guid>? FileIds, List<string>? Principals, Dictionary<string, string>? Metadata, string? Language);

    private sealed record KnowledgeSearchRequest(
        string? Query, string? Collection, int? TopK, Dictionary<string, string>? Filters);
}

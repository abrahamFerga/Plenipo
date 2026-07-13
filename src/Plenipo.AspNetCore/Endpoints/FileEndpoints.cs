using Plenipo.Application.Authorization;
using Plenipo.Application.Files;
using Microsoft.Extensions.Options;

namespace Plenipo.AspNetCore.Endpoints;

/// <summary>
/// The platform file surface: upload (chat attachments) and download. Files are tenant-scoped rows
/// in the platform database with content in the configured blob backend; the agent's document tools
/// operate on the same ids, so "attach a PDF, ask the assistant about it" is one seamless flow.
/// </summary>
public static class FileEndpoints
{
    public static void MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files").WithTags("Files").RequireAuthorization();

        // Upload one file (multipart/form-data). Returns the stored metadata incl. the id that chat
        // messages reference and document tools consume.
        group.MapPost("/", async (
                IFormFile file,
                IFileStore files,
                IOptions<FileStorageOptions> options,
                CancellationToken cancellationToken) =>
            {
                if (file.Length == 0)
                {
                    return Results.BadRequest(new { error = "The file is empty." });
                }

                if (file.Length > options.Value.MaxUploadBytes)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status413PayloadTooLarge,
                        detail: $"The file exceeds the {options.Value.MaxUploadBytes / (1024 * 1024)} MB upload limit.");
                }

                await using var content = file.OpenReadStream();
                var stored = await files.SaveAsync(
                    file.FileName,
                    string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    content,
                    source: "upload",
                    cancellationToken);

                return Results.Created($"/api/files/{stored.Id}", new StoredFileDto(
                    stored.Id, stored.FileName, stored.ContentType, stored.SizeBytes, stored.CreatedAt));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.UploadFiles))
            .DisableAntiforgery()
            .WithName("Files_Upload");

        // Download by id (tenant-scoped: a foreign tenant's id is a 404, not a 403 — no existence leak).
        group.MapGet("/{fileId:guid}", async (
                Guid fileId,
                IFileStore files,
                CancellationToken cancellationToken) =>
            {
                var file = await files.FindAsync(fileId, cancellationToken);
                if (file is null)
                {
                    return Results.NotFound();
                }

                var content = await files.OpenReadAsync(fileId, cancellationToken);
                return content is null
                    ? Results.NotFound()
                    : Results.File(content, file.ContentType, file.FileName);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ReadFiles))
            .WithName("Files_Download");

        // The caller's recent files — powers pickers and the agent's list_documents parity in the UI.
        group.MapGet("/mine", async (IFileStore files, CancellationToken cancellationToken) =>
            {
                var mine = await files.ListMineAsync(50, cancellationToken);
                return Results.Ok(mine.Select(f => new StoredFileDto(f.Id, f.FileName, f.ContentType, f.SizeBytes, f.CreatedAt)));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ReadFiles))
            .WithName("Files_Mine");
    }

    public sealed record StoredFileDto(Guid Id, string FileName, string ContentType, long SizeBytes, DateTimeOffset CreatedAt);
}

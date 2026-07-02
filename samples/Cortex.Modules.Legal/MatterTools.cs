using System.ComponentModel;
using System.Text;
using Cortex.Application.Files;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// Matter-workspace tools — the module's stateful core. Attaching platform files to a matter is the
/// documented "store this PDF as part of the case of Julia Assange" flow: the file id arrives via the
/// chat-attachment convention (any channel), and from then on the matter is the unit the agent reads,
/// drafts, and reports against. Creation and attachment are side-effecting and approval-gated.
/// </summary>
public sealed class MatterTools(LegalDbContext db, IFileStore files, ITenantContext tenant)
{
    [Description("Create a new legal matter (an engagement workspace documents and work product attach to).")]
    public async Task<string> CreateMatter(
        [Description("The matter name, e.g. 'Julia Assange defense' or 'Acme / Initech NDA'.")] string name,
        [Description("Optional client name the matter is for.")] string? clientName = null,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "A matter needs a name.";
        }

        var existing = await FindMatterAsync(trimmed, cancellationToken);
        if (existing is not null)
        {
            return $"A matter named '{existing.Name}' already exists (id {existing.Id}). Use it, or pick a different name.";
        }

        var matter = new Matter
        {
            TenantId = tenant.RequireTenantId(),
            Name = trimmed,
            ClientName = string.IsNullOrWhiteSpace(clientName) ? null : clientName.Trim(),
        };
        db.Matters.Add(matter);
        await db.SaveChangesAsync(cancellationToken);

        return $"Created matter '{matter.Name}'{(matter.ClientName is null ? "" : $" for client {matter.ClientName}")} (id {matter.Id}).";
    }

    [Description("List the tenant's legal matters with their status and document counts.")]
    public async Task<string> ListMatters(CancellationToken cancellationToken = default)
    {
        var matters = await db.Matters
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Name, m.ClientName, m.Status, DocumentCount = m.Documents.Count })
            .Take(50)
            .ToListAsync(cancellationToken);

        if (matters.Count == 0)
        {
            return "No matters yet. Create one with create_matter.";
        }

        var sb = new StringBuilder("Matters (newest first):\n");
        foreach (var m in matters)
        {
            sb.AppendLine($"- {m.Name}{(m.ClientName is null ? "" : $" (client: {m.ClientName})")} — {m.Status}, {m.DocumentCount} document(s)");
        }

        return sb.ToString();
    }

    [Description("Attach a stored file to a legal matter by matter name. Use the file id from the message's attachment reference or from list_documents.")]
    public async Task<string> AttachDocumentToMatter(
        [Description("The stored file id (a GUID) to attach.")] string fileId,
        [Description("The matter name to attach it to (must exist — create_matter first if not).")] string matterName,
        [Description("Optional note, e.g. 'signed original' or 'client draft'.")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(fileId, out var id))
        {
            return $"'{fileId}' is not a valid file id. Use the id from the attachment reference or list_documents.";
        }

        // Tenant-scoped file lookup: a foreign tenant's id is indistinguishable from a missing one.
        var file = await files.FindAsync(id, cancellationToken);
        if (file is null)
        {
            return $"No stored file with id {id} exists. Use list_documents to see available files.";
        }

        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Create it first with create_matter, or use list_matters to find the right name.";
        }

        var alreadyAttached = await db.MatterDocuments
            .AnyAsync(d => d.MatterId == matter.Id && d.FileId == file.Id, cancellationToken);
        if (alreadyAttached)
        {
            return $"'{file.FileName}' is already attached to matter '{matter.Name}'.";
        }

        db.MatterDocuments.Add(new MatterDocument
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            FileId = file.Id,
            FileName = file.FileName,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Attached '{file.FileName}' (file id: {file.Id}) to matter '{matter.Name}'.";
    }

    [Description("List the documents attached to a matter, with the file ids read_document consumes.")]
    public async Task<string> ListMatterDocuments(
        [Description("The matter name.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to see the tenant's matters.";
        }

        var documents = await db.MatterDocuments
            .Where(d => d.MatterId == matter.Id)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        if (documents.Count == 0)
        {
            return $"Matter '{matter.Name}' has no documents yet. Attach one with attach_document_to_matter.";
        }

        var sb = new StringBuilder($"Documents on matter '{matter.Name}':\n");
        foreach (var d in documents)
        {
            sb.AppendLine($"- {d.FileName} (file id: {d.FileId}){(d.Note is null ? "" : $" — {d.Note}")}");
        }

        return sb.ToString();
    }

    private async Task<Matter?> FindMatterAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        return await db.Matters.FirstOrDefaultAsync(
            m => EF.Functions.ILike(m.Name, normalized), cancellationToken);
    }
}

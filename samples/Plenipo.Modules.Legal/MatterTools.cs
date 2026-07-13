using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Plenipo.Application.Connectors;
using Plenipo.Application.Files;
using Plenipo.Application.Jobs;
using Plenipo.Application.Rag;
using Plenipo.Core.Identity;
using Plenipo.Core.Multitenancy;
using Plenipo.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Modules.Legal;

/// <summary>
/// Matter-workspace tools — the module's stateful core. Attaching platform files to a matter is the
/// documented "store this PDF as part of the case of Julia Assange" flow: the file id arrives via the
/// chat-attachment convention (any channel), and from then on the matter is the unit the agent reads,
/// drafts, and reports against. Creation and attachment are side-effecting and approval-gated.
///
/// Every matter lookup honors the ethical wall (<see cref="Matter.RestrictedUserIdsJson"/>): a
/// walled matter is indistinguishable from a missing one to anyone outside the wall. The optional
/// <see cref="IRagService"/> (null when Rag:Enabled is false) backs index_matter_documents.
/// </summary>
public sealed class MatterTools(
    LegalDbContext db,
    IFileStore files,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IJobQueue jobs,
    IConnectorBindingService bindings,
    Plenipo.Application.Documents.IPdfRenderer pdfRenderer,
    IRagService? rag = null)
{
    [Description("Bind a matter to a folder in a connected data source (e.g. the local-folder or azure-blob connector) and start syncing it: new and changed files are attached to the matter and indexed for search_knowledge. One folder per matter; rebinding replaces it. Side-effecting and requires approval.")]
    public async Task<string> ConnectMatterFolder(
        [Description("The matter name to bind.")] string matterName,
        [Description("The folder/prefix within the connector (e.g. 'contracts/acme').")] string folderRef,
        [Description("The connector id (default 'local-folder'; e.g. 'azure-blob').")] string connector = "local-folder",
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var bindingId = await bindings.BindAsync(
            connector, LegalModule.Id, MatterRagGate.MatterResourceType, matter.Id, folderRef.Trim(), cancellationToken);
        var jobId = await jobs.EnqueueAsync(
            LegalModule.Id, ConnectorSyncJob.Kind, new ConnectorSyncArgs(bindingId), cancellationToken);

        return $"Bound matter '{matter.Name}' to '{folderRef}' on the '{connector}' connector and started the first sync. " +
               $"Job id: {jobId} (progress at /api/jobs/{jobId}). Synced files are attached to the matter and indexed; re-run with sync_matter_folder.";
    }

    [Description("Re-sync a matter's bound folder: new and changed files are attached and indexed; unchanged files are skipped. Side-effecting and requires approval.")]
    public async Task<string> SyncMatterFolder(
        [Description("The matter name whose bound folder to sync.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var bindingId = await bindings.FindAsync(
            LegalModule.Id, MatterRagGate.MatterResourceType, matter.Id, cancellationToken);
        if (bindingId is null)
        {
            return $"Matter '{matter.Name}' has no bound folder. Bind one first with connect_matter_folder.";
        }

        var jobId = await jobs.EnqueueAsync(
            LegalModule.Id, ConnectorSyncJob.Kind, new ConnectorSyncArgs(bindingId.Value), cancellationToken);
        return $"Started syncing matter '{matter.Name}' from its bound folder. Job id: {jobId} (progress at /api/jobs/{jobId}).";
    }

    [Description("Start a bulk review of ALL documents on a matter: every document is checked against every question, and the finished review table is filed on the matter as a PDF. Runs in the background.")]
    public async Task<string> StartBulkReview(
        [Description("The matter name whose documents to review.")] string matterName,
        [Description("The questions to answer per document, separated by semicolons or newlines.")] string questions,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var parsed = questions
            .Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(q => q.Trim())
            .Where(q => q.Length > 0)
            .ToList();
        if (parsed.Count == 0)
        {
            return "Provide at least one question to answer per document.";
        }

        var documentCount = await db.MatterDocuments.CountAsync(d => d.MatterId == matter.Id, cancellationToken);
        if (documentCount == 0)
        {
            return $"Matter '{matter.Name}' has no documents to review. Attach documents first.";
        }

        var jobId = await jobs.EnqueueAsync(
            LegalModule.Id, BulkReviewJobHandler.JobKind,
            new BulkReviewArgs(matter.Id, matter.Name, parsed), cancellationToken);

        return $"Started a bulk review of {documentCount} document(s) on matter '{matter.Name}' against {parsed.Count} question(s). " +
               $"Job id: {jobId} (progress at /api/jobs/{jobId}). The review table will be filed on the matter as a PDF when it completes — check list_matter_documents.";
    }

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
        // Walls are per-user identity, so filter in memory after the tenant-scoped fetch.
        var matters = (await db.Matters
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new { m.Name, m.ClientName, m.Status, m.RestrictedUserIdsJson, DocumentCount = m.Documents.Count })
                .Take(200)
                .ToListAsync(cancellationToken))
            .Where(m => Matter.WallAllows(m.RestrictedUserIdsJson, currentUser.UserId))
            .Take(50)
            .ToList();

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

    [Description("Restrict a matter behind an ethical wall: afterwards ONLY you (and users you later add via the admin surface) can see or use the matter, its documents, and its knowledge collection. Side-effecting and requires approval.")]
    public async Task<string> RestrictMatterAccess(
        [Description("The matter name to restrict.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var userId = currentUser.UserId;
        if (userId is null)
        {
            return "Cannot restrict a matter without an authenticated user.";
        }

        matter.RestrictedUserIdsJson = JsonSerializer.Serialize(new[] { userId.Value });
        await db.SaveChangesAsync(cancellationToken);

        return $"Matter '{matter.Name}' is now behind an ethical wall: only you can see or use it. " +
               "Lift it with open_matter_access.";
    }

    [Description("Lift a matter's ethical wall so the whole tenant can see it again. Only someone inside the wall can lift it. Side-effecting and requires approval.")]
    public async Task<string> OpenMatterAccess(
        [Description("The matter name to open up.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        // FindMatterAsync already applies the wall, so an outsider can't even name the matter.
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (matter.RestrictedUserIdsJson is null)
        {
            return $"Matter '{matter.Name}' is not restricted.";
        }

        matter.RestrictedUserIdsJson = null;
        await db.SaveChangesAsync(cancellationToken);
        return $"Matter '{matter.Name}' is open to the whole tenant again.";
    }

    [Description("Index ALL documents on a matter into its searchable knowledge collection, so search_knowledge can answer questions across them with citations. Runs in the background; re-running refreshes the index. Side-effecting and requires approval.")]
    public async Task<string> IndexMatterDocuments(
        [Description("The matter name whose documents to index.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        if (rag is null)
        {
            return "The knowledge pipeline is not enabled on this deployment (Rag:Enabled is false).";
        }

        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var fileIds = await db.MatterDocuments
            .Where(d => d.MatterId == matter.Id)
            .OrderBy(d => d.FileName)
            .Select(d => d.FileId)
            .ToListAsync(cancellationToken);
        if (fileIds.Count == 0)
        {
            return $"Matter '{matter.Name}' has no documents to index. Attach documents first.";
        }

        var collectionId = await rag.GetOrCreateCollectionAsync(
            LegalModule.Id, MatterRagGate.MatterResourceType, matter.Id, $"matter: {matter.Name}", cancellationToken);
        var jobId = await jobs.EnqueueAsync(
            LegalModule.Id, RagIngestJob.Kind, new RagIngestArgs(collectionId, fileIds), cancellationToken);

        return $"Started indexing {fileIds.Count} document(s) on matter '{matter.Name}' into collection 'matter: {matter.Name}'. " +
               $"Job id: {jobId} (progress at /api/jobs/{jobId}). Once it completes, search_knowledge can answer questions across the matter with citations.";
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

    [Description("Docket a deadline on a matter: a court date, filing deadline, or limitation period. A reminder notification fires ahead of the due date.")]
    public async Task<string> AddDeadline(
        [Description("The matter name to docket the deadline on.")] string matterName,
        [Description("What is due, e.g. 'Answer to complaint' or 'Discovery cutoff'.")] string title,
        [Description("When it is due — an ISO date or date-time, e.g. 2026-08-14 or 2026-08-14T17:00Z.")] string dueDate,
        [Description("Optional notes (court, judge, rule reference).")] string? notes = null,
        [Description("Days before the due date to send the reminder (default 3).")] int remindDaysBefore = 3,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (!DateTimeOffset.TryParse(dueDate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dueAt))
        {
            return $"'{dueDate}' is not a date I can parse — use an ISO date like 2026-08-14 (optionally with a time).";
        }

        db.MatterDeadlines.Add(new MatterDeadline
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            Title = title.Trim(),
            DueAt = dueAt,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            OwnerUserId = currentUser.UserId,
            ReminderDaysBefore = Math.Clamp(remindDaysBefore, 0, 90),
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Docketed '{title.Trim()}' on matter '{matter.Name}', due {dueAt:yyyy-MM-dd}. " +
               $"A reminder will be sent {Math.Clamp(remindDaysBefore, 0, 90)} day(s) before.";
    }

    [Description("List upcoming deadlines — across all matters, or for one matter. Soonest first; overdue items are flagged.")]
    public async Task<string> ListDeadlines(
        [Description("Optional matter name to filter to; omit for all matters.")] string? matterName = null,
        CancellationToken cancellationToken = default)
    {
        Matter? matter = null;
        if (!string.IsNullOrWhiteSpace(matterName))
        {
            matter = await FindMatterAsync(matterName, cancellationToken);
            if (matter is null)
            {
                return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
            }
        }

        var query = db.MatterDeadlines.Where(d => d.CompletedAt == null);
        if (matter is not null)
        {
            query = query.Where(d => d.MatterId == matter.Id);
        }

        var deadlines = await query
            .OrderBy(d => d.DueAt)
            .Take(50)
            .Join(db.Matters.Where(m => m.Status == MatterStatus.Open), d => d.MatterId, m => m.Id,
                (d, m) => new { d.Title, d.DueAt, d.Notes, MatterName = m.Name, m.RestrictedUserIdsJson })
            .ToListAsync(cancellationToken);

        // Walled matters keep their dates invisible to outsiders, like every other matter surface.
        var visible = deadlines.Where(d => Matter.WallAllows(d.RestrictedUserIdsJson, currentUser.UserId)).ToList();
        if (visible.Count == 0)
        {
            return matter is null
                ? "No open deadlines are docketed. Add one with add_deadline."
                : $"Matter '{matter.Name}' has no open deadlines. Add one with add_deadline.";
        }

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder("Open deadlines (soonest first):\n");
        foreach (var d in visible)
        {
            var days = (int)Math.Ceiling((d.DueAt - now).TotalDays);
            var when = days < 0 ? $"OVERDUE by {-days} day(s)" : days == 0 ? "due TODAY" : $"in {days} day(s)";
            sb.AppendLine($"- {d.DueAt:yyyy-MM-dd} · {d.Title} — matter '{d.MatterName}' ({when})" +
                          (d.Notes is null ? "" : $" — {d.Notes}"));
        }

        return sb.ToString();
    }

    [Description("Mark a docketed deadline as completed so it leaves the upcoming list and stops reminding.")]
    public async Task<string> CompleteDeadline(
        [Description("The matter name the deadline is on.")] string matterName,
        [Description("The deadline title (as shown by list_deadlines).")] string title,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var normalized = title.Trim();
        var deadline = await db.MatterDeadlines.FirstOrDefaultAsync(
            d => d.MatterId == matter.Id && d.CompletedAt == null && EF.Functions.ILike(d.Title, normalized),
            cancellationToken);
        if (deadline is null)
        {
            return $"No open deadline titled '{normalized}' on matter '{matter.Name}'. Check list_deadlines for the exact title.";
        }

        deadline.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return $"Marked '{deadline.Title}' on matter '{matter.Name}' as completed.";
    }

    [Description("Record a party on a matter (the client, an adverse party, or a related person/entity) — the raw material of future conflict checks. Record parties at intake and as they emerge.")]
    public async Task<string> AddParty(
        [Description("The matter name to record the party on.")] string matterName,
        [Description("The party's full name, e.g. 'Initech Corporation' or 'Jane Doe'.")] string name,
        [Description("The party's role: client, adverse, or related.")] string role = "client",
        [Description("Optional context, e.g. 'parent company of client'.")] string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (!Enum.TryParse<PartyRole>(role, ignoreCase: true, out var parsedRole))
        {
            return $"'{role}' is not a party role — use client, adverse, or related.";
        }

        db.MatterParties.Add(new MatterParty
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            Name = name.Trim(),
            Role = parsedRole,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        });
        await db.SaveChangesAsync(cancellationToken);
        return $"Recorded '{name.Trim()}' as {parsedRole.ToString().ToUpperInvariant()} party on matter '{matter.Name}'.";
    }

    [Description("List the parties recorded on a matter, with their roles.")]
    public async Task<string> ListParties(
        [Description("The matter name.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var parties = await db.MatterParties
            .Where(p => p.MatterId == matter.Id)
            .OrderBy(p => p.Role).ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);
        if (parties.Count == 0)
        {
            return $"Matter '{matter.Name}' has no recorded parties. Record them with add_party — conflict checks depend on it.";
        }

        var sb = new StringBuilder($"Parties on matter '{matter.Name}':\n");
        foreach (var p in parties)
        {
            sb.AppendLine($"- {p.Name} — {p.Role.ToString().ToUpperInvariant()}{(p.Notes is null ? "" : $" ({p.Notes})")}");
        }

        return sb.ToString();
    }

    [Description("Run a conflict-of-interest check BEFORE opening a matter or engaging a new client: searches every recorded party and client name across all of the firm's matters. Restricted (walled) matters report as anonymous hits.")]
    public async Task<string> CheckConflicts(
        [Description("The name(s) to check — a person or entity, or several separated by semicolons. Free text is fine; names are matched loosely.")] string names,
        CancellationToken cancellationToken = default)
    {
        // The check runs across the WHOLE tenant — including walled matters, because a conflict
        // that hides behind an ethical wall is still a conflict. Walled hits are reported without
        // the matter or party name (the standard screened-matter convention).
        var parties = await db.MatterParties
            .Join(db.Matters, p => p.MatterId, m => m.Id,
                (p, m) => new { p.Name, p.Role, MatterName = m.Name, m.RestrictedUserIdsJson })
            .Take(5000)
            .ToListAsync(cancellationToken);
        var clients = await db.Matters
            .Where(m => m.ClientName != null)
            .Select(m => new { Name = m.ClientName!, Role = PartyRole.Client, MatterName = m.Name, m.RestrictedUserIdsJson })
            .Take(2000)
            .ToListAsync(cancellationToken);

        var visible = new List<string>();
        var restrictedHits = 0;
        foreach (var candidate in parties.Concat(clients))
        {
            if (!ConflictCheck.Matches(names, candidate.Name))
            {
                continue;
            }

            if (Matter.WallAllows(candidate.RestrictedUserIdsJson, currentUser.UserId))
            {
                visible.Add($"- '{candidate.Name}' is a {candidate.Role.ToString().ToUpperInvariant()} party on matter '{candidate.MatterName}'");
            }
            else
            {
                restrictedHits++;
            }
        }

        if (visible.Count == 0 && restrictedHits == 0)
        {
            return $"CONFLICT CHECK CLEAR: no recorded party or client matches '{names.Trim()}'. " +
                   "Reliability depends on parties being recorded — keep using add_party at intake.";
        }

        var sb = new StringBuilder($"CONFLICT CHECK: {visible.Count + restrictedHits} potential conflict(s) found:\n");
        foreach (var line in visible)
        {
            sb.AppendLine(line);
        }

        if (restrictedHits > 0)
        {
            sb.AppendLine($"- {restrictedHits} additional match(es) on RESTRICTED matter(s) — details are screened; contact your administrator before proceeding.");
        }

        sb.Append("Do not open the engagement until these are cleared by the responsible attorney.");
        return sb.ToString();
    }

    [Description("Log time worked on a matter (billable by default). Quick capture: not approval-gated — entries are append-only and correctable with a follow-up entry.")]
    public async Task<string> LogTime(
        [Description("The matter name the time was spent on.")] string matterName,
        [Description("Hours worked, e.g. 0.5 or 2.")] double hours,
        [Description("What was done — the narrative line for the bill, e.g. 'Drafted NDA; call with client'.")] string description,
        [Description("The day the work happened as an ISO date (default: today).")] string? date = null,
        [Description("Whether the time is billable (default true).")] bool billable = true,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (hours is <= 0 or > 24)
        {
            return "hours must be greater than 0 and at most 24 per entry.";
        }

        var workedOn = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out workedOn))
        {
            return $"'{date}' is not a date I can parse — use an ISO date like 2026-07-05, or omit it for today.";
        }

        db.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            UserId = currentUser.UserId,
            UserDisplay = currentUser.DisplayName,
            Hours = (decimal)hours,
            Description = description.Trim(),
            WorkedOn = workedOn,
            Billable = billable,
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Logged {hours:0.##}h on matter '{matter.Name}' for {workedOn:yyyy-MM-dd}" +
               $"{(billable ? "" : " (non-billable)")}: {description.Trim()}";
    }

    [Description("List logged time: one matter's entries with totals, or (with no matter) the caller's own recent time across matters.")]
    public async Task<string> ListTime(
        [Description("Optional matter name; omit for your own recent time across all matters.")] string? matterName = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(matterName))
        {
            var matter = await FindMatterAsync(matterName, cancellationToken);
            if (matter is null)
            {
                return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
            }

            var entries = await db.TimeEntries
                .Where(t => t.MatterId == matter.Id)
                .OrderByDescending(t => t.WorkedOn)
                .Take(100)
                .ToListAsync(cancellationToken);
            if (entries.Count == 0)
            {
                return $"No time logged on matter '{matter.Name}' yet. Log some with log_time.";
            }

            var sb = new StringBuilder($"Time on matter '{matter.Name}':\n");
            foreach (var e in entries)
            {
                sb.AppendLine($"- {e.WorkedOn:yyyy-MM-dd} · {e.Hours:0.##}h · {e.Description}" +
                              $"{(e.Billable ? "" : " (non-billable)")}{(e.UserDisplay is null ? "" : $" — {e.UserDisplay}")}");
            }

            sb.Append($"Total: {entries.Sum(e => e.Hours):0.##}h ({entries.Where(e => e.Billable).Sum(e => e.Hours):0.##}h billable).");
            return sb.ToString();
        }

        // No matter: the caller's own last 14 days, grouped per matter — the "what did I do this
        // week" view. Walls are irrelevant here: these are the caller's own entries.
        var since = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime).AddDays(-14);
        var mine = await db.TimeEntries
            .Where(t => t.UserId == currentUser.UserId && t.WorkedOn >= since)
            .Join(db.Matters, t => t.MatterId, m => m.Id, (t, m) => new { t.Hours, t.Billable, t.WorkedOn, MatterName = m.Name })
            .OrderByDescending(t => t.WorkedOn)
            .Take(200)
            .ToListAsync(cancellationToken);
        if (mine.Count == 0)
        {
            return "You have no time logged in the last 14 days. Log some with log_time (matter, hours, what you did).";
        }

        var byMatter = mine.GroupBy(t => t.MatterName)
            .OrderByDescending(g => g.Sum(t => t.Hours));
        var summary = new StringBuilder($"Your time, last 14 days ({mine.Sum(t => t.Hours):0.##}h total):\n");
        foreach (var g in byMatter)
        {
            summary.AppendLine($"- {g.Key}: {g.Sum(t => t.Hours):0.##}h ({g.Where(t => t.Billable).Sum(t => t.Hours):0.##}h billable)");
        }

        return summary.ToString();
    }

    [Description("Add a task (to-do) on a matter, optionally assigned to someone by name with a target date. For hard dates with reminder obligations use add_deadline instead.")]
    public async Task<string> AddTask(
        [Description("The matter name.")] string matterName,
        [Description("What needs doing, e.g. 'Draft the motion to dismiss'.")] string title,
        [Description("Optional assignee by name, e.g. 'Maria' or 'paralegal team'.")] string? assignedTo = null,
        [Description("Optional target date as an ISO date, e.g. 2026-08-01.")] string? dueDate = null,
        [Description("Optional notes.")] string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        DateOnly? dueOn = null;
        if (!string.IsNullOrWhiteSpace(dueDate))
        {
            if (!DateOnly.TryParse(dueDate, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return $"'{dueDate}' is not a date I can parse — use an ISO date like 2026-08-01, or omit it.";
            }

            dueOn = parsed;
        }

        db.MatterTasks.Add(new MatterTask
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            Title = title.Trim(),
            AssignedTo = string.IsNullOrWhiteSpace(assignedTo) ? null : assignedTo.Trim(),
            DueOn = dueOn,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedByUserId = currentUser.UserId,
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Added task '{title.Trim()}' on matter '{matter.Name}'" +
               $"{(assignedTo is null ? "" : $", assigned to {assignedTo.Trim()}")}" +
               $"{(dueOn is null ? "" : $", target {dueOn:yyyy-MM-dd}")}.";
    }

    [Description("List open tasks — across all matters, or for one matter. Completed tasks are excluded.")]
    public async Task<string> ListTasks(
        [Description("Optional matter name to filter to; omit for all matters.")] string? matterName = null,
        CancellationToken cancellationToken = default)
    {
        Matter? matter = null;
        if (!string.IsNullOrWhiteSpace(matterName))
        {
            matter = await FindMatterAsync(matterName, cancellationToken);
            if (matter is null)
            {
                return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
            }
        }

        var query = db.MatterTasks.Where(t => t.CompletedAt == null);
        if (matter is not null)
        {
            query = query.Where(t => t.MatterId == matter.Id);
        }

        var tasks = await query
            .OrderBy(t => t.DueOn == null) // dated tasks first, soonest first
            .ThenBy(t => t.DueOn)
            .ThenBy(t => t.CreatedAt)
            .Take(100)
            .Join(db.Matters.Where(m => m.Status == MatterStatus.Open), t => t.MatterId, m => m.Id,
                (t, m) => new { t.Title, t.AssignedTo, t.DueOn, t.Notes, MatterName = m.Name, m.RestrictedUserIdsJson })
            .ToListAsync(cancellationToken);

        var visible = tasks.Where(t => Matter.WallAllows(t.RestrictedUserIdsJson, currentUser.UserId)).ToList();
        if (visible.Count == 0)
        {
            return matter is null
                ? "No open tasks. Add one with add_task."
                : $"Matter '{matter.Name}' has no open tasks. Add one with add_task.";
        }

        var sb = new StringBuilder("Open tasks:\n");
        foreach (var t in visible)
        {
            sb.AppendLine($"- {t.Title} — matter '{t.MatterName}'" +
                          $"{(t.AssignedTo is null ? "" : $", assigned to {t.AssignedTo}")}" +
                          $"{(t.DueOn is null ? "" : $", target {t.DueOn:yyyy-MM-dd}")}" +
                          $"{(t.Notes is null ? "" : $" — {t.Notes}")}");
        }

        return sb.ToString();
    }

    [Description("Mark a task on a matter as completed so it leaves the open list.")]
    public async Task<string> CompleteTask(
        [Description("The matter name the task is on.")] string matterName,
        [Description("The task title (as shown by list_tasks).")] string title,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var normalized = title.Trim();
        var task = await db.MatterTasks.FirstOrDefaultAsync(
            t => t.MatterId == matter.Id && t.CompletedAt == null && EF.Functions.ILike(t.Title, normalized),
            cancellationToken);
        if (task is null)
        {
            return $"No open task titled '{normalized}' on matter '{matter.Name}'. Check list_tasks for the exact title.";
        }

        task.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return $"Marked task '{task.Title}' on matter '{matter.Name}' as completed.";
    }

    [Description("The one-look brief on a matter: status, parties, open deadlines (overdue flagged), open tasks, time totals, and recent documents. Use to answer 'brief me on X' or before working a matter.")]
    public async Task<string> GetMatterOverview(
        [Description("The matter name.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var now = DateTimeOffset.UtcNow;
        var parties = await db.MatterParties.Where(p => p.MatterId == matter.Id)
            .OrderBy(p => p.Role).ThenBy(p => p.Name).Take(20).ToListAsync(cancellationToken);
        var deadlines = await db.MatterDeadlines.Where(d => d.MatterId == matter.Id && d.CompletedAt == null)
            .OrderBy(d => d.DueAt).Take(10).ToListAsync(cancellationToken);
        var tasks = await db.MatterTasks.Where(t => t.MatterId == matter.Id && t.CompletedAt == null)
            .OrderBy(t => t.DueOn == null).ThenBy(t => t.DueOn).Take(10).ToListAsync(cancellationToken);
        var time = await db.TimeEntries.Where(t => t.MatterId == matter.Id).ToListAsync(cancellationToken);
        var documents = await db.MatterDocuments.Where(d => d.MatterId == matter.Id)
            .OrderByDescending(d => d.CreatedAt).Take(5).ToListAsync(cancellationToken);
        var documentCount = await db.MatterDocuments.CountAsync(d => d.MatterId == matter.Id, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"MATTER BRIEF: {matter.Name}");
        sb.AppendLine($"Status: {matter.Status}{(matter.ClientName is null ? "" : $" · Client: {matter.ClientName}")}" +
                      $"{(matter.RestrictedUserIdsJson is null ? "" : " · RESTRICTED (ethical wall)")}");

        sb.AppendLine(parties.Count == 0
            ? "Parties: none recorded — record them with add_party (conflict checks depend on it)."
            : "Parties: " + string.Join("; ", parties.Select(p => $"{p.Name} ({p.Role.ToString().ToUpperInvariant()})")));

        if (deadlines.Count == 0)
        {
            sb.AppendLine("Deadlines: none open.");
        }
        else
        {
            sb.AppendLine("Open deadlines:");
            foreach (var d in deadlines)
            {
                var days = (int)Math.Ceiling((d.DueAt - now).TotalDays);
                var when = days < 0 ? $"OVERDUE by {-days} day(s)" : days == 0 ? "due TODAY" : $"in {days} day(s)";
                sb.AppendLine($"  - {d.DueAt:yyyy-MM-dd} · {d.Title} ({when})");
            }
        }

        if (tasks.Count == 0)
        {
            sb.AppendLine("Tasks: none open.");
        }
        else
        {
            sb.AppendLine("Open tasks:");
            foreach (var t in tasks)
            {
                sb.AppendLine($"  - {t.Title}{(t.AssignedTo is null ? "" : $" (assigned to {t.AssignedTo})")}" +
                              $"{(t.DueOn is null ? "" : $", target {t.DueOn:yyyy-MM-dd}")}");
            }
        }

        sb.AppendLine(time.Count == 0
            ? "Time: none logged."
            : $"Time: {time.Sum(t => t.Hours):0.##}h total, {time.Where(t => t.Billable).Sum(t => t.Hours):0.##}h billable across {time.Count} entr(ies).");

        sb.AppendLine(documentCount == 0
            ? "Documents: none attached."
            : $"Documents ({documentCount}): " + string.Join("; ", documents.Select(d => d.FileName)) +
              (documentCount > documents.Count ? " …" : ""));

        return sb.ToString();
    }

    [Description("Generate a PRE-BILL for a matter: its time entries over an optional date range with billable totals, rendered as a PDF and filed on the matter for billing review.")]
    public async Task<string> ExportPrebill(
        [Description("The matter name.")] string matterName,
        [Description("Optional period start as an ISO date (inclusive); omit for all time.")] string? fromDate = null,
        [Description("Optional period end as an ISO date (inclusive); omit for today.")] string? toDate = null,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var from = DateOnly.MinValue;
        if (!string.IsNullOrWhiteSpace(fromDate) && !DateOnly.TryParse(fromDate, System.Globalization.CultureInfo.InvariantCulture, out from))
        {
            return $"'{fromDate}' is not a date I can parse — use an ISO date like 2026-07-01, or omit it.";
        }

        var to = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(toDate) && !DateOnly.TryParse(toDate, System.Globalization.CultureInfo.InvariantCulture, out to))
        {
            return $"'{toDate}' is not a date I can parse — use an ISO date like 2026-07-31, or omit it for today.";
        }

        var entries = await db.TimeEntries
            .Where(t => t.MatterId == matter.Id && t.WorkedOn >= from && t.WorkedOn <= to)
            .OrderBy(t => t.WorkedOn).ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return $"No time entries on matter '{matter.Name}' in that period — nothing to pre-bill.";
        }

        var billable = entries.Where(e => e.Billable).Sum(e => e.Hours);
        var nonBillable = entries.Where(e => !e.Billable).Sum(e => e.Hours);
        var period = $"{(from == DateOnly.MinValue ? "inception" : from.ToString("yyyy-MM-dd"))} – {to:yyyy-MM-dd}";

        var body = new StringBuilder();
        body.AppendLine($"Matter: {matter.Name}{(matter.ClientName is null ? "" : $"   Client: {matter.ClientName}")}");
        body.AppendLine($"Period: {period}   Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd}");
        body.AppendLine();
        foreach (var e in entries)
        {
            body.AppendLine($"{e.WorkedOn:yyyy-MM-dd}  {e.Hours,5:0.##}h  {e.Description}" +
                            $"{(e.Billable ? "" : "  [non-billable]")}{(e.UserDisplay is null ? "" : $"  — {e.UserDisplay}")}");
        }

        body.AppendLine();
        body.AppendLine($"Billable: {billable:0.##}h   Non-billable: {nonBillable:0.##}h   Total: {billable + nonBillable:0.##}h");
        body.AppendLine();
        body.AppendLine("Draft pre-bill for internal billing review — not an invoice.");

        // Render + store + file on the matter in one step, so the pre-bill can't end up orphaned.
        var pdf = pdfRenderer.Render($"Pre-bill — {matter.Name}", body.ToString());
        using var stream = new MemoryStream(pdf);
        var stored = await files.SaveAsync(
            $"prebill-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf", "application/pdf", stream,
            source: "prebill", cancellationToken);

        db.MatterDocuments.Add(new MatterDocument
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            FileId = stored.Id,
            FileName = stored.FileName,
            Note = $"pre-bill {period}: {billable:0.##}h billable of {billable + nonBillable:0.##}h",
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Filed pre-bill '{stored.FileName}' (file id: {stored.Id}) on matter '{matter.Name}': " +
               $"{entries.Count} entr(ies), {billable:0.##}h billable, {nonBillable:0.##}h non-billable ({period}).";
    }

    [Description("Close a matter with a completeness check: refuses while deadlines or tasks are still open (finish them, or pass force after confirming with the user). Closed matters stop reminding.")]
    public async Task<string> CloseMatter(
        [Description("The matter name to close.")] string matterName,
        [Description("Close even with open deadlines/tasks (only after the user explicitly confirms).")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (matter.Status == MatterStatus.Closed)
        {
            return $"Matter '{matter.Name}' is already closed.";
        }

        // The close-out check — the discipline that keeps a statute deadline from dying inside a
        // closed file. Open work blocks the close unless the user explicitly forces it.
        var openDeadlines = await db.MatterDeadlines
            .CountAsync(d => d.MatterId == matter.Id && d.CompletedAt == null, cancellationToken);
        var openTasks = await db.MatterTasks
            .CountAsync(t => t.MatterId == matter.Id && t.CompletedAt == null, cancellationToken);

        if ((openDeadlines > 0 || openTasks > 0) && !force)
        {
            return $"CANNOT CLOSE '{matter.Name}': {openDeadlines} open deadline(s) and {openTasks} open task(s) remain. " +
                   "Complete them (complete_deadline / complete_task), or — only if the user explicitly confirms — close with force. " +
                   "Use get_matter_overview to see what is open.";
        }

        matter.Status = MatterStatus.Closed;
        await db.SaveChangesAsync(cancellationToken);
        return $"Closed matter '{matter.Name}'." +
               (openDeadlines > 0 || openTasks > 0
                   ? $" WARNING (forced): {openDeadlines} open deadline(s) and {openTasks} open task(s) were left open; reminders for this matter stop."
                   : " Nothing was left open.");
    }

    [Description("Reopen a closed matter (its deadlines and tasks become active again).")]
    public async Task<string> ReopenMatter(
        [Description("The matter name to reopen.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (matter.Status == MatterStatus.Open)
        {
            return $"Matter '{matter.Name}' is already open.";
        }

        matter.Status = MatterStatus.Open;
        await db.SaveChangesAsync(cancellationToken);
        return $"Reopened matter '{matter.Name}'. Its open deadlines and tasks are active again.";
    }

    [Description("Draft a CLIENT-FACING status update letter for a matter (recent progress, upcoming dates, hours worked — no internal notes or strategy) and file it on the matter as a PDF for attorney review before sending.")]
    public async Task<string> DraftStatusUpdate(
        [Description("The matter name.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var now = DateTimeOffset.UtcNow;
        var since = now.AddDays(-30);
        var recentDeadlines = await db.MatterDeadlines
            .Where(d => d.MatterId == matter.Id && d.CompletedAt >= since)
            .OrderByDescending(d => d.CompletedAt).Take(10).ToListAsync(cancellationToken);
        var recentTasks = await db.MatterTasks
            .Where(t => t.MatterId == matter.Id && t.CompletedAt >= since)
            .OrderByDescending(t => t.CompletedAt).Take(10).ToListAsync(cancellationToken);
        var upcoming = await db.MatterDeadlines
            .Where(d => d.MatterId == matter.Id && d.CompletedAt == null && d.DueAt <= now.AddDays(60))
            .OrderBy(d => d.DueAt).Take(10).ToListAsync(cancellationToken);
        var sinceDay = DateOnly.FromDateTime(since.UtcDateTime);
        var recentHours = await db.TimeEntries
            .Where(t => t.MatterId == matter.Id && t.WorkedOn >= sinceDay)
            .SumAsync(t => (decimal?)t.Hours, cancellationToken) ?? 0m;

        // Client-facing on purpose: progress, dates, and effort — no internal notes, assignees,
        // billing rates, or strategy. The letter is a DRAFT the attorney reviews before sending.
        var body = new StringBuilder();
        body.AppendLine($"Re: {matter.Name}");
        body.AppendLine($"Date: {now:yyyy-MM-dd}");
        body.AppendLine();
        body.AppendLine($"Dear {matter.ClientName ?? "Client"},");
        body.AppendLine();
        body.AppendLine("Here is the current status of your matter.");
        body.AppendLine();

        if (recentDeadlines.Count > 0 || recentTasks.Count > 0)
        {
            body.AppendLine("Progress in the last 30 days:");
            foreach (var d in recentDeadlines)
            {
                body.AppendLine($"  - Completed: {d.Title} ({d.CompletedAt:yyyy-MM-dd})");
            }

            foreach (var t in recentTasks)
            {
                body.AppendLine($"  - Completed: {t.Title} ({t.CompletedAt:yyyy-MM-dd})");
            }
        }
        else
        {
            body.AppendLine("Progress in the last 30 days: work is ongoing; no milestones completed in this period.");
        }

        body.AppendLine();
        if (upcoming.Count > 0)
        {
            body.AppendLine("Upcoming dates:");
            foreach (var d in upcoming)
            {
                body.AppendLine($"  - {d.DueAt:yyyy-MM-dd}: {d.Title}");
            }
        }
        else
        {
            body.AppendLine("Upcoming dates: none scheduled in the next 60 days.");
        }

        body.AppendLine();
        body.AppendLine($"Time devoted to your matter in the last 30 days: {recentHours:0.##} hours.");
        body.AppendLine();
        body.AppendLine("Please contact us with any questions.");
        body.AppendLine();
        body.AppendLine("Sincerely,");
        body.AppendLine("[Attorney name]");
        body.AppendLine();
        body.AppendLine("DRAFT — for attorney review before sending to the client.");

        var pdf = pdfRenderer.Render($"Status update — {matter.Name}", body.ToString());
        using var stream = new MemoryStream(pdf);
        var stored = await files.SaveAsync(
            $"status-update-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf", "application/pdf", stream,
            source: "status_update", cancellationToken);

        db.MatterDocuments.Add(new MatterDocument
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            FileId = stored.Id,
            FileName = stored.FileName,
            Note = $"client status update (draft), {now:yyyy-MM-dd}",
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Filed draft status update '{stored.FileName}' (file id: {stored.Id}) on matter '{matter.Name}': " +
               $"{recentDeadlines.Count + recentTasks.Count} completed item(s), {upcoming.Count} upcoming date(s), " +
               $"{recentHours:0.##}h in the last 30 days. Review before sending to the client.";
    }

    private async Task<Matter?> FindMatterAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var matter = await db.Matters.FirstOrDefaultAsync(
            m => EF.Functions.ILike(m.Name, normalized), cancellationToken);

        // The ethical wall: outside it, a walled matter is indistinguishable from a missing one —
        // the same no-existence-leak stance the platform takes for cross-tenant ids.
        return matter is not null && matter.IsAccessibleTo(currentUser.UserId) ? matter : null;
    }
}

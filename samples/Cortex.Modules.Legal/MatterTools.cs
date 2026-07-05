using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Cortex.Application.Connectors;
using Cortex.Application.Files;
using Cortex.Application.Jobs;
using Cortex.Application.Rag;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

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
            .Join(db.Matters, d => d.MatterId, m => m.Id,
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

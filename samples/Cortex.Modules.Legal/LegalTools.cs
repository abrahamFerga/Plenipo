using System.ComponentModel;
using System.Text;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// The Legal module's clause and playbook tools, backed by the tenant's persisted library (seeded
/// from <see cref="LegalCatalog"/> defaults, curated by the firm from there). Deterministic: the
/// tools search and render — the module's instructions keep the assistant from giving legal advice.
/// </summary>
public sealed class LegalTools(LegalDbContext db, ITenantContext tenant)
{
    [Description("Search the firm's clause library by keyword. Returns matching clauses with their category and a short summary.")]
    public async Task<string> SearchClauses(
        [Description("Keywords, e.g. 'confidentiality', 'liability', or 'termination'.")] string query,
        CancellationToken cancellationToken = default)
    {
        var matches = await SearchLibraryAsync(query, cancellationToken);
        if (matches.Count == 0)
        {
            return $"No clauses match \"{query}\". Try a keyword like 'confidentiality', 'liability', or 'termination'.";
        }

        var lines = matches.Take(8).Select(c => $"{c.Title} ({c.Category}) — {c.Summary}");
        return $"Found {matches.Count} clause(s): {string.Join(" | ", lines)}.";
    }

    [Description("Draft a contract clause from the firm's library, filled in with the two party names. Use search_clauses first if unsure of the clause type.")]
    public async Task<string> DraftClause(
        [Description("Clause type or keyword, e.g. 'indemnification' or 'governing law'.")] string clauseType,
        [Description("Name of the first party (e.g. the provider/discloser).")] string partyA,
        [Description("Name of the second party (e.g. the client/recipient).")] string partyB,
        CancellationToken cancellationToken = default)
    {
        var matches = await SearchLibraryAsync(clauseType, cancellationToken);
        var clause = matches.Count > 0 ? matches[0] : null;
        if (clause is null)
        {
            return $"No clause in the firm's library matches \"{clauseType}\". Call search_clauses to find an available clause type first.";
        }

        var body = LegalCatalog.RenderTemplate(clause.Template, partyA, partyB);
        return $"{clause.Title} ({clause.Category}):\n\n{body}\n\nThis is a standard template, not legal advice — have a licensed attorney review before use.";
    }

    [Description("Get the firm's contract-review playbook: the rules to check contracts against, with severity.")]
    public async Task<string> GetPlaybook(CancellationToken cancellationToken = default)
    {
        // Severity persists as a string (readable rows), so order in memory where enum order applies.
        var rules = (await db.PlaybookRules.ToListAsync(cancellationToken))
            .OrderByDescending(r => r.Severity)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rules.Count == 0)
        {
            return "The firm's playbook is empty. An administrator can add rules via the playbook endpoints.";
        }

        var sb = new StringBuilder("Firm playbook (check contracts against every rule):\n");
        foreach (var rule in rules)
        {
            sb.AppendLine($"- [{rule.Severity}] {rule.Title}: {rule.Guidance}");
        }

        return sb.ToString();
    }

    [Description("Save (add or replace) a clause in the firm's library. Curating the library is how drafting reflects the firm's own precedent — use {PartyA}/{PartyB} placeholders in the template. Side-effecting and requires approval.")]
    public async Task<string> SaveClause(
        [Description("The clause's stable type/name, e.g. 'confidentiality' or 'data protection'.")] string clauseType,
        [Description("Display title, e.g. 'Data Protection'.")] string title,
        [Description("Category, e.g. 'Protection', 'Risk allocation', 'Commercial'.")] string category,
        [Description("One-sentence summary of what the clause does.")] string summary,
        [Description("The clause text; {PartyA} and {PartyB} are substituted at draft time.")] string template,
        CancellationToken cancellationToken = default)
    {
        var slug = LibraryCuration.Slugify(clauseType);
        if (slug.Length == 0 || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(template))
        {
            return "A clause needs a type/name, a title, and the clause text.";
        }

        var existing = await db.Clauses.FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken);
        if (existing is null)
        {
            db.Clauses.Add(new TenantClause
            {
                TenantId = tenant.RequireTenantId(),
                Slug = slug,
                Title = title.Trim(),
                Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
                Summary = string.IsNullOrWhiteSpace(summary) ? title.Trim() : summary.Trim(),
                Template = template.Trim(),
            });
        }
        else
        {
            existing.Title = title.Trim();
            existing.Category = string.IsNullOrWhiteSpace(category) ? existing.Category : category.Trim();
            existing.Summary = string.IsNullOrWhiteSpace(summary) ? existing.Summary : summary.Trim();
            existing.Template = template.Trim();
        }

        await db.SaveChangesAsync(cancellationToken);
        var placeholders = template.Contains("{PartyA}", StringComparison.Ordinal) || template.Contains("{PartyB}", StringComparison.Ordinal)
            ? ""
            : " Note: the template has no {PartyA}/{PartyB} placeholders, so drafts won't substitute party names.";
        return $"{(existing is null ? "Added" : "Updated")} clause '{title.Trim()}' ('{slug}') in the firm's library.{placeholders}";
    }

    [Description("Remove a clause from the firm's library by its type/name.")]
    public async Task<string> RemoveClause(
        [Description("The clause type/name as shown by search_clauses (e.g. 'non-compete').")] string clauseType,
        CancellationToken cancellationToken = default)
    {
        var slug = LibraryCuration.Slugify(clauseType);
        var clause = await db.Clauses.FirstOrDefaultAsync(c => c.Slug == slug, cancellationToken);
        if (clause is null)
        {
            return $"No clause '{slug}' exists in the library. Use search_clauses to find the exact type.";
        }

        db.Clauses.Remove(clause);
        await db.SaveChangesAsync(cancellationToken);
        // The intake workflow drafts the engagement letter from this clause — degrade loudly.
        var warning = slug == "engagement-letter"
            ? " WARNING: the intake workflow drafts engagement letters from this clause; save a replacement or intake step 4 will fail."
            : "";
        return $"Removed clause '{clause.Title}' ('{slug}') from the firm's library.{warning}";
    }

    [Description("Add a rule to the firm's contract-review playbook — what reviews check contracts against. Side-effecting and requires approval.")]
    public async Task<string> AddPlaybookRule(
        [Description("Short rule title, e.g. 'Uncapped liability'.")] string title,
        [Description("What to check and what to flag, written for the reviewing agent.")] string guidance,
        [Description("Severity: info, caution, or critical.")] string severity = "caution",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(guidance))
        {
            return "A playbook rule needs a title and guidance.";
        }

        if (!Enum.TryParse<RuleSeverity>(severity, ignoreCase: true, out var parsed))
        {
            return $"'{severity}' is not a severity — use info, caution, or critical.";
        }

        db.PlaybookRules.Add(new PlaybookRule
        {
            TenantId = tenant.RequireTenantId(),
            Title = title.Trim(),
            Guidance = guidance.Trim(),
            Severity = parsed,
        });
        await db.SaveChangesAsync(cancellationToken);
        return $"Added [{parsed}] playbook rule '{title.Trim()}'. Every future contract review checks against it.";
    }

    [Description("Remove a rule from the firm's contract-review playbook by its title.")]
    public async Task<string> RemovePlaybookRule(
        [Description("The rule title as shown by get_playbook.")] string title,
        CancellationToken cancellationToken = default)
    {
        var normalized = title.Trim();
        var rule = await db.PlaybookRules.FirstOrDefaultAsync(
            r => EF.Functions.ILike(r.Title, normalized), cancellationToken);
        if (rule is null)
        {
            return $"No playbook rule titled '{normalized}'. Check get_playbook for the exact title.";
        }

        db.PlaybookRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        return $"Removed playbook rule '{rule.Title}'. Future reviews no longer check against it.";
    }

    private async Task<IReadOnlyList<TenantClause>> SearchLibraryAsync(string query, CancellationToken cancellationToken)
    {
        // Tenant libraries are small (seed is 8 rows); load once and reuse the shared forgiving search.
        var clauses = await db.Clauses.OrderBy(c => c.Title).Take(500).ToListAsync(cancellationToken);
        return LegalCatalog.Search(clauses, query, c => [c.Title, c.Category, c.Summary, c.Slug]);
    }
}

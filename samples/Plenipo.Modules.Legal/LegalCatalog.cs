namespace Plenipo.Modules.Legal;

/// <summary>A standard contract clause: reference data the agent searches and renders.</summary>
public sealed record Clause(string Id, string Title, string Category, string Summary, string Template);

/// <summary>A rendered clause with the parties substituted in.</summary>
public sealed record RenderedClause(string Title, string Category, string Body);

/// <summary>
/// The default clause library — the SEED for each tenant's editable clause table (see
/// <c>LegalModule.SeedAsync</c>), plus the shared search/render helpers both the seed data and the
/// tenant's persisted clauses use. Templates use {PartyA} / {PartyB} placeholders.
/// </summary>
public static class LegalCatalog
{
    public static readonly IReadOnlyList<Clause> Clauses =
    [
        new("confidentiality", "Confidentiality", "Protection",
            "Each party keeps the other's confidential information secret and uses it only for the agreement's purpose.",
            "Each party (the \"Receiving Party\") shall keep confidential all non-public information disclosed by the other party ({PartyA} or {PartyB}) and shall not use it except to perform its obligations under this Agreement."),
        new("indemnification", "Indemnification", "Risk allocation",
            "One party covers the other's losses arising from defined claims.",
            "{PartyA} shall indemnify and hold harmless {PartyB} from any claims, damages, and reasonable legal fees arising out of {PartyA}'s breach of this Agreement or negligent acts."),
        new("limitation-of-liability", "Limitation of Liability", "Risk allocation",
            "Caps each party's liability and excludes indirect damages.",
            "Neither {PartyA} nor {PartyB} shall be liable for indirect or consequential damages, and each party's total liability shall not exceed the fees paid under this Agreement in the twelve months preceding the claim."),
        new("termination", "Termination", "Lifecycle",
            "How and when either party may end the agreement.",
            "Either {PartyA} or {PartyB} may terminate this Agreement on thirty (30) days' written notice, or immediately if the other party materially breaches and fails to cure within fifteen (15) days."),
        new("governing-law", "Governing Law", "General",
            "Which jurisdiction's law governs the contract.",
            "This Agreement between {PartyA} and {PartyB} shall be governed by and construed in accordance with the laws of the agreed jurisdiction, without regard to its conflict-of-laws rules."),
        new("payment-terms", "Payment Terms", "Commercial",
            "When and how invoices are paid.",
            "{PartyB} shall pay {PartyA} within thirty (30) days of receipt of a valid invoice. Undisputed overdue amounts accrue interest at 1.5% per month."),
        new("ip-assignment", "Intellectual Property Assignment", "IP",
            "Assigns ownership of work product to one party.",
            "All work product created by {PartyA} under this Agreement shall be the exclusive property of {PartyB}, and {PartyA} hereby assigns all right, title, and interest in such work product to {PartyB}."),
        new("non-compete", "Non-Compete", "Restrictive covenant",
            "Restricts a party from competing for a defined period and area (enforceability varies by jurisdiction).",
            "For twelve (12) months after termination, {PartyA} shall not engage in a business that directly competes with {PartyB} within the agreed territory, to the extent permitted by applicable law."),
        new("engagement-letter", "Engagement Letter", "Engagement",
            "The letter that opens a representation: scope, fees, responsibilities, and termination. The intake workflow's work product.",
            "ENGAGEMENT LETTER\n\n{PartyA} (the \"Firm\") is pleased to represent {PartyB} (the \"Client\") in the matter described below.\n\n" +
            "1. SCOPE. The Firm's representation is limited to the matter described in this letter; services outside that scope require a separate engagement.\n" +
            "2. FEES AND BILLING. The Client agrees to pay the Firm's fees at its standard hourly rates, plus reasonable expenses, invoiced monthly and payable within thirty (30) days.\n" +
            "3. CLIENT RESPONSIBILITIES. The Client will provide complete and accurate information and respond promptly to the Firm's requests.\n" +
            "4. NO GUARANTEE. The Firm will exercise professional skill and judgment; no outcome is or can be guaranteed.\n" +
            "5. TERMINATION. Either the Firm or the Client may terminate the engagement on written notice, subject to applicable rules of professional conduct; fees for work performed remain payable.\n\n" +
            "Please confirm your agreement by countersigning below."),
    ];

    /// <summary>Case-insensitive search over title, category, summary, and id.</summary>
    public static IEnumerable<Clause> Search(string query) =>
        Search(Clauses, query, c => [c.Title, c.Category, c.Summary, c.Id]);

    /// <summary>
    /// The shared two-stage search: exact-phrase match first, then a forgiving fallback so a
    /// natural-language query ("draft a confidentiality clause") still matches — an item hits if any
    /// significant word (≥ 4 chars, skipping short stop-words) of the query appears in one of its
    /// searchable fields. Used by both the seed catalog and the tenant's persisted clauses.
    /// </summary>
    public static IReadOnlyList<T> Search<T>(IEnumerable<T> items, string query, Func<T, string[]> fields)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var q = query.Trim();
        var all = items as IReadOnlyList<T> ?? [.. items];
        var exact = all.Where(i => Matches(fields(i), q)).ToArray();
        if (exact.Length > 0)
        {
            return exact;
        }

        var words = q.Split([' ', ',', '.', ';', ':', '?', '!', '-', '/', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .ToArray();
        return [.. all.Where(i => words.Any(w => Matches(fields(i), w)))];
    }

    private static bool Matches(string[] fields, string term) =>
        fields.Any(f => f.Contains(term, StringComparison.OrdinalIgnoreCase));

    /// <summary>Renders a clause's template, substituting the two party names.</summary>
    public static RenderedClause Render(Clause clause, string partyA, string partyB) =>
        new(clause.Title, clause.Category, RenderTemplate(clause.Template, partyA, partyB));

    /// <summary>Substitutes {PartyA} / {PartyB} in any clause template (seeded or tenant-authored).</summary>
    public static string RenderTemplate(string template, string partyA, string partyB) =>
        template
            .Replace("{PartyA}", string.IsNullOrWhiteSpace(partyA) ? "Party A" : partyA.Trim(), StringComparison.Ordinal)
            .Replace("{PartyB}", string.IsNullOrWhiteSpace(partyB) ? "Party B" : partyB.Trim(), StringComparison.Ordinal);

    /// <summary>The default firm playbook rules seeded for a new tenant (editable from there).</summary>
    public static readonly IReadOnlyList<(string Title, string Guidance, Persistence.RuleSeverity Severity)> DefaultPlaybook =
    [
        ("Uncapped liability", "Flag any contract without a limitation-of-liability clause, or where liability is uncapped or exceeds twelve months of fees.", Persistence.RuleSeverity.Critical),
        ("Unilateral termination", "Flag termination rights that only one party holds, or cure periods shorter than fifteen days.", Persistence.RuleSeverity.Critical),
        ("Missing confidentiality", "Flag agreements that exchange non-public information without a confidentiality clause.", Persistence.RuleSeverity.Caution),
        ("Broad indemnification", "Flag indemnities covering indirect or consequential damages, or ones not limited to the indemnifying party's breach or negligence.", Persistence.RuleSeverity.Caution),
        ("Auto-renewal", "Note evergreen/auto-renewal terms and their notice windows so deadlines can be docketed in the firm's calendar system.", Persistence.RuleSeverity.Info),
    ];
}

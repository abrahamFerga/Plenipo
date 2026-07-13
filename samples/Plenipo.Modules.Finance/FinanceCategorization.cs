namespace Plenipo.Modules.Finance;

/// <summary>How a category was determined — mirrors the-ledger's CategorizationSource.</summary>
public enum CategorizationSource
{
    /// <summary>No deterministic rule matched; the agent should reason about it.</summary>
    None = 0,

    /// <summary>A learned, tenant-specific rule matched (highest confidence).</summary>
    LearnedRule = 1,

    /// <summary>A built-in merchant keyword rule matched.</summary>
    DefaultRule = 2,

    /// <summary>The category was set directly by a user (a correction).</summary>
    Manual = 3,
}

/// <summary>Outcome of the deterministic categorization fast-path.</summary>
public sealed record CategorizationOutcome(string? Category, CategorizationSource Source, double Confidence)
{
    public static readonly CategorizationOutcome Unmatched = new(null, CategorizationSource.None, 0d);
}

/// <summary>
/// Deterministic transaction categorizer, ported from the-ledger's <c>RuleCategorizer</c> /
/// <c>DefaultCategoryRules</c> (ADR-0004 fast-path). It applies learned tenant rules first, then
/// built-in merchant keyword rules. When nothing matches it returns <see cref="CategorizationOutcome.Unmatched"/>
/// so the caller can defer to the LLM — in Plenipo that fallback is the agent itself, not a second
/// categorizer service. The learned-rule store is a TODO pending the Finance module's own persistence.
/// </summary>
public static class FinanceCategorization
{
    /// <summary>System categories (names), ported from the-ledger's SystemCategories.</summary>
    public static readonly IReadOnlyList<string> Categories =
    [
        "Income", "Groceries", "Dining", "Transport", "Utilities",
        "Shopping", "Health", "Entertainment", "Transfers", "Other",
    ];

    /// <summary>
    /// Built-in keyword → category rules. Ported from the-ledger's Mexican-merchant defaults and
    /// extended with a few internationally-recognizable merchants. Matched by case-insensitive contains.
    /// </summary>
    private static readonly (string Keyword, string Category)[] DefaultRules =
    [
        // Groceries
        ("OXXO", "Groceries"), ("WALMART", "Groceries"), ("SORIANA", "Groceries"),
        ("CHEDRAUI", "Groceries"), ("ALDI", "Groceries"), ("SUPER", "Groceries"),
        // Utilities
        ("CFE", "Utilities"), ("TELMEX", "Utilities"), ("IZZI", "Utilities"),
        ("TOTALPLAY", "Utilities"), ("AGUA", "Utilities"), ("ELECTRIC", "Utilities"),
        // Transport
        ("UBER", "Transport"), ("DIDI", "Transport"), ("LYFT", "Transport"),
        ("PEMEX", "Transport"), ("GASOLIN", "Transport"), ("SHELL", "Transport"),
        // Income
        ("NOMINA", "Income"), ("DEPOSITO", "Income"), ("SPEI RECIBIDO", "Income"), ("PAYROLL", "Income"),
        // Dining
        ("STARBUCKS", "Dining"), ("RESTAURANTE", "Dining"), ("RESTAURANT", "Dining"),
        ("MCDONALD", "Dining"), ("CAFE", "Dining"),
        // Shopping
        ("AMAZON", "Shopping"), ("MERPAGO", "Shopping"), ("MERCADO", "Shopping"), ("LIVERPOOL", "Shopping"),
        // Entertainment
        ("SPOTIFY", "Entertainment"), ("NETFLIX", "Entertainment"), ("CINEPOLIS", "Entertainment"),
        // Health
        ("FARMACIA", "Health"), ("HOSPITAL", "Health"), ("PHARMACY", "Health"),
    ];

    public static CategorizationOutcome Categorize(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var upper = description.ToUpperInvariant();

        // TODO: learned tenant rules (highest priority) once the Finance module has its own DbContext —
        // mirrors the-ledger's CategorizationRule store populated from user corrections.

        foreach (var (keyword, category) in DefaultRules)
        {
            if (upper.Contains(keyword, StringComparison.Ordinal))
            {
                return new CategorizationOutcome(category, CategorizationSource.DefaultRule, 0.85d);
            }
        }

        return CategorizationOutcome.Unmatched;
    }

    /// <summary>
    /// Derives a learnable match pattern from a transaction description — the first alphanumeric token
    /// of at least three characters, upper-cased (e.g. "OXXO TIENDA 55" → "OXXO"). Returns <c>null</c>
    /// when nothing usable is found. Used when a user correction is turned into a learned rule.
    /// </summary>
    public static string? DeriveMatchPattern(string description)
    {
        ArgumentNullException.ThrowIfNull(description);

        foreach (var token in description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = new string([.. token.Where(char.IsLetterOrDigit)]);
            if (cleaned.Length >= 3)
            {
                return cleaned.ToUpperInvariant();
            }
        }

        return null;
    }
}

using System.ComponentModel;
using Plenipo.Modules.Finance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Modules.Finance;

/// <summary>
/// The categorization tool, now tenant-aware. Ported from the-ledger's <c>CompositeCategorizer</c>:
/// learned tenant rules first, then built-in merchant rules, then defer to the agent. The learned
/// rules come from the module's own (tenant-filtered) database.
/// </summary>
public sealed class FinanceCategorizer(FinanceDbContext db)
{
    [Description("Categorize a financial transaction into a spending category from its description and amount. " +
                 "Consults the tenant's learned rules first, then built-in merchant rules; if nothing matches it " +
                 "reports so and lists the categories for you to choose from.")]
    public async Task<string> CategorizeTransactionAsync(
        [Description("The transaction description or merchant name.")] string description,
        [Description("The transaction amount in the account currency (negative for credits/deposits).")] decimal amount,
        CancellationToken cancellationToken)
    {
        var outcome = await ResolveAsync(description, cancellationToken);

        return outcome.Source switch
        {
            CategorizationSource.LearnedRule => $"Category: {outcome.Category} (learned rule, confidence {outcome.Confidence:0.00}).",
            CategorizationSource.DefaultRule => $"Category: {outcome.Category} (built-in merchant rule, confidence {outcome.Confidence:0.00}).",
            _ => $"No deterministic rule matched \"{description}\" (amount {amount:0.##}). " +
                 $"Choose the most appropriate category from: {string.Join(", ", FinanceCategorization.Categories)}.",
        };
    }

    /// <summary>
    /// Resolves a category via the deterministic chain (learned tenant rules → built-in merchant rules),
    /// returning <see cref="CategorizationOutcome.Unmatched"/> when neither applies. Used both by the
    /// agent tool and by transaction ingestion.
    /// </summary>
    public async Task<CategorizationOutcome> ResolveAsync(string description, CancellationToken cancellationToken)
    {
        var upper = description.ToUpperInvariant();

        var learned = await db.CategorizationRules
            .OrderByDescending(r => r.Priority)
            .ToListAsync(cancellationToken);
        foreach (var rule in learned)
        {
            if (upper.Contains(rule.MatchPattern.ToUpperInvariant(), StringComparison.Ordinal))
            {
                return new CategorizationOutcome(rule.Category, CategorizationSource.LearnedRule, 0.95d);
            }
        }

        return FinanceCategorization.Categorize(description);
    }
}

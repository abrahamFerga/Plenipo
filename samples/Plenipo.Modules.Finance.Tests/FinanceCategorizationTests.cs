using Plenipo.Modules.Finance;

namespace Plenipo.Modules.Finance.Tests;

/// <summary>
/// Locks in the behaviour ported from the-ledger's RuleCategorizer / DefaultCategoryRules: built-in
/// merchant keyword rules match (case-insensitively), and anything unmatched is handed back as
/// <see cref="CategorizationSource.None"/> so the agent can reason about it.
/// </summary>
public sealed class FinanceCategorizationTests
{
    [Theory]
    [InlineData("OXXO TIENDA 1234", "Groceries")]
    [InlineData("WALMART SUPERCENTER", "Groceries")]
    [InlineData("UBER TRIP HELP.UBER.COM", "Transport")]
    [InlineData("PEMEX ESTACION 4490", "Transport")]
    [InlineData("NETFLIX.COM", "Entertainment")]
    [InlineData("SPOTIFY P0A1B2", "Entertainment")]
    [InlineData("CFE SUMINISTRADOR", "Utilities")]
    [InlineData("FARMACIA GUADALAJARA", "Health")]
    [InlineData("STARBUCKS COFFEE", "Dining")]
    [InlineData("AMAZON MX MARKETPLACE", "Shopping")]
    public void KnownMerchant_MapsToExpectedCategory(string description, string expected)
    {
        var outcome = FinanceCategorization.Categorize(description);

        Assert.Equal(expected, outcome.Category);
        Assert.Equal(CategorizationSource.DefaultRule, outcome.Source);
        Assert.True(outcome.Confidence > 0.5d);
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        var outcome = FinanceCategorization.Categorize("pago oxxo gas");
        Assert.Equal("Groceries", outcome.Category);
    }

    [Fact]
    public void IncomeKeywords_MapToIncome()
    {
        Assert.Equal("Income", FinanceCategorization.Categorize("DEPOSITO NOMINA QUINCENAL").Category);
        Assert.Equal("Income", FinanceCategorization.Categorize("SPEI RECIBIDO BANCO").Category);
    }

    [Fact]
    public void UnknownMerchant_IsUnmatched_SoTheAgentDecides()
    {
        var outcome = FinanceCategorization.Categorize("Joe's Artisan Bakery #42");

        Assert.Null(outcome.Category);
        Assert.Equal(CategorizationSource.None, outcome.Source);
        Assert.Equal(0d, outcome.Confidence);
    }

    [Fact]
    public void Categories_CoverTheTenSystemCategories()
    {
        Assert.Equal(10, FinanceCategorization.Categories.Count);
        Assert.Contains("Income", FinanceCategorization.Categories);
        Assert.Contains("Other", FinanceCategorization.Categories);
    }

    [Theory]
    [InlineData("OXXO TIENDA 55", "OXXO")]
    [InlineData("Joe's Bakery #42", "JOES")]
    [InlineData("UBER * EATS", "UBER")]
    [InlineData("a b cd", null)]
    public void DeriveMatchPattern_TakesFirstSignificantToken(string description, string? expected)
    {
        Assert.Equal(expected, FinanceCategorization.DeriveMatchPattern(description));
    }
}

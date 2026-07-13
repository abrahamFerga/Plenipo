using Plenipo.Modules.Nutrition;

namespace Plenipo.Modules.Nutrition.Tests;

public sealed class NutritionCatalogTests
{
    [Fact]
    public void Search_IsCaseInsensitive_AndMatchesSubstrings()
    {
        var hits = NutritionCatalog.Search("chicken").ToList();
        Assert.Single(hits);
        Assert.Equal("Chicken breast", hits[0].Name);

        Assert.NotEmpty(NutritionCatalog.Search("RICE"));
    }

    [Fact]
    public void Search_BlankQuery_ReturnsNothing()
    {
        Assert.Empty(NutritionCatalog.Search("   "));
    }

    [Fact]
    public void Estimate_ScalesPer100gValuesByPortion()
    {
        var chicken = NutritionCatalog.Search("chicken").First();

        var estimate = NutritionCatalog.Estimate(chicken, 200);

        // 200 g of chicken breast: 2x the per-100g values.
        Assert.Equal(330, estimate.Kcal);
        Assert.Equal(62, estimate.ProteinG);
        Assert.Equal(7.2, estimate.FatG, 1);
        Assert.Equal(0, estimate.CarbG);
    }

    [Fact]
    public void EstimateMeal_UnknownFood_AsksToSearchFirst()
    {
        var result = new NutritionTools().EstimateMeal("unicorn steak", 100);
        Assert.Contains("search_foods", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_DeclaresItsToolsAndDiaryTab()
    {
        var manifest = new NutritionModule().Manifest;

        Assert.Equal("nutrition", manifest.Id);

        // The catalog tools plus the persisted-diary tools.
        var toolNames = manifest.Tools.Select(t => t.Name).ToArray();
        Assert.Contains("search_foods", toolNames);
        Assert.Contains("estimate_meal", toolNames);
        Assert.Contains("log_meal", toolNames);
        Assert.Contains("summarize_diary", toolNames);

        Assert.Contains(manifest.Tabs, t => t.Id == "chat");

        // The Diary tab is now a real server-driven data tab (no longer a placeholder).
        var diary = manifest.Tabs.Single(t => t.Id == "diary");
        Assert.Equal("/api/nutrition/diary", diary.DataEndpoint);
        Assert.Null(diary.Placeholder);
    }
}

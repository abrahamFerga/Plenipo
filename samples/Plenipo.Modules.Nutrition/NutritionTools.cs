using System.ComponentModel;

namespace Plenipo.Modules.Nutrition;

/// <summary>
/// The Nutrition module's agent tools. Stateless and deterministic: the agent searches the catalog and
/// estimates portions, then narrates. Mirrors NutriForge's SearchFoods / portion-estimate surface.
/// </summary>
public sealed class NutritionTools
{
    [Description("Search the food catalog by name or keywords. Returns matching foods with per-100g calories and macros.")]
    public string SearchFoods(
        [Description("Food name or keywords, e.g. 'chicken' or 'banana'.")] string query)
    {
        var matches = NutritionCatalog.Search(query).Take(8).ToList();
        if (matches.Count == 0)
        {
            return $"No catalog foods match \"{query}\". Try a single food name like 'chicken' or 'banana'.";
        }

        var lines = matches.Select(f =>
            $"{f.Name} — {f.KcalPer100g:0} kcal, protein {f.ProteinPer100g:0.#} g, fat {f.FatPer100g:0.#} g, carbs {f.CarbPer100g:0.#} g (per 100 g)");
        return $"Found {matches.Count} food(s): {string.Join("; ", lines)}.";
    }

    [Description("Estimate the calories and macros for a portion of a catalog food, given grams.")]
    public string EstimateMeal(
        [Description("Food name as it appears in the catalog (use search_foods first if unsure).")] string foodName,
        [Description("Portion size in grams.")] double grams)
    {
        var food = NutritionCatalog.Search(foodName).FirstOrDefault();
        if (food is null)
        {
            return $"No catalog food matches \"{foodName}\". Call search_foods to find the right name first.";
        }

        var e = NutritionCatalog.Estimate(food, grams);
        return $"{e.Grams:0} g of {e.Food}: {e.Kcal:0} kcal, protein {e.ProteinG:0.#} g, fat {e.FatG:0.#} g, carbs {e.CarbG:0.#} g.";
    }
}

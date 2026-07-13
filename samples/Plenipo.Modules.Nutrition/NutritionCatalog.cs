namespace Plenipo.Modules.Nutrition;

/// <summary>A catalog food with per-100g calories and macros.</summary>
public sealed record Food(string Name, double KcalPer100g, double ProteinPer100g, double FatPer100g, double CarbPer100g);

/// <summary>Estimated calories and macros for a specific portion.</summary>
public sealed record MealEstimate(string Food, double Grams, double Kcal, double ProteinG, double FatG, double CarbG);

/// <summary>
/// A small built-in food catalog and the deterministic math over it. Mirrors NutriForge's principle
/// (ADR-0004): deterministic code owns every number; the agent only decides which tool to call and
/// narrates the result. Real deployments swap this for the full catalog service.
/// </summary>
public static class NutritionCatalog
{
    public static readonly IReadOnlyList<Food> Foods =
    [
        new("Chicken breast", 165, 31, 3.6, 0),
        new("Salmon", 208, 20, 13, 0),
        new("Egg", 155, 13, 11, 1.1),
        new("White rice (cooked)", 130, 2.7, 0.3, 28),
        new("Oats", 389, 17, 7, 66),
        new("Banana", 89, 1.1, 0.3, 23),
        new("Apple", 52, 0.3, 0.2, 14),
        new("Broccoli", 34, 2.8, 0.4, 7),
        new("Almonds", 579, 21, 50, 22),
        new("Greek yogurt", 59, 10, 0.4, 3.6),
        new("Avocado", 160, 2, 15, 9),
        new("Black beans (cooked)", 132, 8.9, 0.5, 24),
    ];

    public static IEnumerable<Food> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var q = query.Trim();
        var exact = Foods.Where(f => f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length > 0)
        {
            return exact;
        }

        // Forgiving fallback so a natural-language query ("how many calories in an avocado?") still finds the
        // food: match if any significant word (≥ 4 chars, skipping short stop-words) of the query is a
        // substring of the food name.
        var words = q.Split([' ', ',', '.', ';', ':', '?', '!', '-', '/', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .ToArray();
        return Foods.Where(f => words.Any(w => f.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public static MealEstimate Estimate(Food food, double grams)
    {
        ArgumentNullException.ThrowIfNull(food);
        var factor = grams / 100d;
        return new MealEstimate(
            food.Name,
            grams,
            Math.Round(food.KcalPer100g * factor, 0),
            Math.Round(food.ProteinPer100g * factor, 1),
            Math.Round(food.FatPer100g * factor, 1),
            Math.Round(food.CarbPer100g * factor, 1));
    }
}

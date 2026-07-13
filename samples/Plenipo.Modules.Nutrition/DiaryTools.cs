using System.ComponentModel;
using Plenipo.Core.Multitenancy;
using Plenipo.Modules.Nutrition.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Plenipo.Modules.Nutrition;

/// <summary>
/// The Nutrition module's stateful agent tools: log a meal to the tenant's food diary and summarize the
/// day. Macros are computed deterministically from the catalog, then persisted — the agent decides which
/// tool to call and narrates; the code owns the numbers and the storage.
/// </summary>
public sealed class DiaryTools(NutritionDbContext db, ITenantContext tenant)
{
    [Description("Log a meal to the food diary: records the food, the portion in grams, and the computed calories and macros for today.")]
    public async Task<string> LogMeal(
        [Description("Food name as it appears in the catalog (use search_foods first if unsure).")] string foodName,
        [Description("Portion size in grams.")] double grams)
    {
        var food = NutritionCatalog.Search(foodName).FirstOrDefault();
        if (food is null)
        {
            return $"No catalog food matches \"{foodName}\". Call search_foods to find the right name first.";
        }
        if (grams <= 0)
        {
            return "Portion must be greater than zero grams.";
        }

        var estimate = NutritionCatalog.Estimate(food, grams);
        db.DiaryEntries.Add(new DiaryEntry
        {
            TenantId = tenant.RequireTenantId(),
            Date = DateOnly.FromDateTime(DateTime.UtcNow),
            FoodName = estimate.Food,
            Grams = estimate.Grams,
            Kcal = estimate.Kcal,
            ProteinG = estimate.ProteinG,
            FatG = estimate.FatG,
            CarbG = estimate.CarbG,
        });
        await db.SaveChangesAsync();

        return $"Logged {estimate.Grams:0} g of {estimate.Food}: {estimate.Kcal:0} kcal " +
               $"(protein {estimate.ProteinG:0.#} g, fat {estimate.FatG:0.#} g, carbs {estimate.CarbG:0.#} g).";
    }

    [Description("Summarize the food diary over the last N days (default today): total calories and macros.")]
    public async Task<string> SummarizeDiary(
        [Description("How many days back to include, counting today. Defaults to 1 (today only).")] int days = 1)
    {
        var span = Math.Clamp(days, 1, 31);
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-(span - 1));
        var entries = await db.DiaryEntries.Where(d => d.Date >= since).ToListAsync();
        if (entries.Count == 0)
        {
            return span == 1 ? "No meals logged today yet." : $"No meals logged in the last {span} days.";
        }

        var scope = span == 1 ? "today" : $"the last {span} days";
        return $"Over {scope} you logged {entries.Count} meal(s): {entries.Sum(e => e.Kcal):0} kcal total " +
               $"(protein {entries.Sum(e => e.ProteinG):0.#} g, fat {entries.Sum(e => e.FatG):0.#} g, carbs {entries.Sum(e => e.CarbG):0.#} g).";
    }
}

using Plenipo.Core.Entities;

namespace Plenipo.Modules.Nutrition.Persistence;

/// <summary>
/// A logged meal in the tenant's food diary. The macros are computed deterministically from the catalog
/// at log time (the agent never invents numbers) and stored, so the Diary tab and the daily summary read
/// straight from persisted rows.
/// </summary>
public sealed class DiaryEntry : TenantEntityBase
{
    public DateOnly Date { get; set; }
    public required string FoodName { get; set; }
    public double Grams { get; set; }
    public double Kcal { get; set; }
    public double ProteinG { get; set; }
    public double FatG { get; set; }
    public double CarbG { get; set; }
}

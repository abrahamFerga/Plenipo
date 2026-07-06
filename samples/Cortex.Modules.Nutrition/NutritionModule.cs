using Cortex.Application.Authorization;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Nutrition.Persistence;
using Cortex.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Modules.Nutrition;

/// <summary>
/// The Nutrition vertical (NutriForge) — the platform's second domain module, proving Cortex supports
/// many industries from one codebase. It owns a small <c>nutrition</c> schema for the persisted food
/// diary; the catalog and portion math stay stateless reference logic.
/// </summary>
public sealed class NutritionModule : IModule
{
    public const string Id = "nutrition";

    public const string ViewFoods = "nutrition.foods.view";
    public const string ViewDiary = "nutrition.diary.view";

    public ModuleManifest Manifest { get; } = new()
    {
        Id = Id,
        DisplayName = "Nutrition",
        Version = "1.0.0",
        Description = "Nutrition coach. Search foods, estimate meals, and track calories and macros toward your goals.",
        Icon = "salad",
        AgentInstructions =
            "You are Cortex's nutrition assistant. Use search_foods to find catalog items and estimate_meal to " +
            "compute calories and macros for a portion. Use log_meal to record what the user actually ate to their " +
            "diary, and summarize_diary to report their daily totals. Let the tools own the numbers; never invent " +
            "nutrition values. Be encouraging and practical. Do not give medical advice; suggest a professional for " +
            "clinical needs.",
        SuggestedPrompts =
        [
            "Search for chicken",
            "Log 150 g of salmon",
            "What did I eat today?",
        ],
        Roles = ["nutrition:user", "nutrition:admin"],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "search_foods",
                Description = "Search the food catalog by name; returns per-100g calories and macros.",
                Permission = Permissions.ForTool(Id, "search_foods"),
            },
            new ToolDescriptor
            {
                Name = "estimate_meal",
                Description = "Estimate calories and macros for a portion of a catalog food.",
                Permission = Permissions.ForTool(Id, "estimate_meal"),
            },
            new ToolDescriptor
            {
                Name = "log_meal",
                Description = "Log a meal (food + grams) to the tenant's food diary.",
                Permission = Permissions.ForTool(Id, "log_meal"),
                Audit = true,
            },
            new ToolDescriptor
            {
                Name = "summarize_diary",
                Description = "Summarize the food diary over recent days (calories and macros).",
                Permission = Permissions.ForTool(Id, "summarize_diary"),
            },
        ],
        Tabs =
        [
            new TabDescriptor { Id = "chat", Label = "Chat", Route = "/nutrition/chat", Icon = "message-circle", Order = 0 },
            new TabDescriptor
            {
                Id = "diary", Label = "Diary", Route = "/nutrition/diary", Icon = "book-open", Order = 1,
                Permission = ViewDiary,
                DataEndpoint = "/api/nutrition/diary",
                Columns =
                [
                    new("date", "Date"), new("foodName", "Food"), new("grams", "g"), new("kcal", "kcal"),
                    new("proteinG", "Protein"), new("fatG", "Fat"), new("carbG", "Carbs"),
                ],
                // Add-only (no KeyField/DeleteEndpoint): entries are records of what was eaten, and the
                // endpoint computes the macros from the catalog — the form only takes food + portion.
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/nutrition/diary",
                    Permission = Permissions.ForTool(Id, "log_meal"),
                    Fields =
                    [
                        new("foodName", "Food (catalog name)"),
                        new("grams", "Portion (grams)", Numeric: true),
                        new("date", "Date (YYYY-MM-DD, blank = today)", Required: false),
                    ],
                },
            },
            new TabDescriptor
            {
                Id = "foods", Label = "Foods", Route = "/nutrition/foods", Icon = "apple", Order = 2,
                Permission = ViewFoods,
                DataEndpoint = "/api/nutrition/foods",
                Columns =
                [
                    new("name", "Food"), new("kcalPer100g", "kcal/100g"), new("proteinPer100g", "Protein"),
                    new("fatPer100g", "Fat"), new("carbPer100g", "Carbs"),
                ],
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<NutritionTools>();
        services.AddScoped<DiaryTools>();
        services.AddSingleton<IModuleToolSource, NutritionToolSource>();

        // The module owns its food diary under the 'nutrition' schema of the platform database.
        services.AddDbContext<NutritionDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString(NutritionDbContext.ConnectionName)));
    }

    public async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<NutritionDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/nutrition").WithTags("Nutrition").RequireAuthorization();

        // The food catalog (reference data, not tenant-scoped).
        group.MapGet("/foods", (string? query) =>
            {
                var foods = string.IsNullOrWhiteSpace(query)
                    ? NutritionCatalog.Foods
                    : [.. NutritionCatalog.Search(query)];
                return Results.Ok(foods);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewFoods))
            .WithName("Nutrition_GetFoods");

        // The tenant's food diary (most recent first) — drives the Diary tab.
        group.MapGet("/diary", async (NutritionDbContext db, CancellationToken cancellationToken) =>
            {
                var items = await db.DiaryEntries
                    .OrderByDescending(d => d.Date)
                    .ThenByDescending(d => d.CreatedAt)
                    .Take(200)
                    .Select(d => new DiaryEntryDto(d.Id, d.Date, d.FoodName, d.Grams, d.Kcal, d.ProteinG, d.FatG, d.CarbG))
                    .ToListAsync(cancellationToken);
                return Results.Ok(items);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewDiary))
            .WithName("Nutrition_GetDiary");

        // Log a meal directly (the agent's log_meal tool persists the same way). Macros are computed from
        // the catalog server-side — the client only supplies the food and portion.
        group.MapPost("/diary", async (
                LogMealRequest body, NutritionDbContext db, ITenantContext tenant, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(body.FoodName))
                {
                    return Results.BadRequest("A food name is required.");
                }
                var food = NutritionCatalog.Search(body.FoodName).FirstOrDefault();
                if (food is null)
                {
                    return Results.BadRequest($"No catalog food matches '{body.FoodName}'.");
                }
                if (body.Grams <= 0)
                {
                    return Results.BadRequest("Portion must be greater than zero grams.");
                }

                var estimate = NutritionCatalog.Estimate(food, body.Grams);
                var entry = new DiaryEntry
                {
                    TenantId = tenant.RequireTenantId(),
                    Date = body.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    FoodName = estimate.Food,
                    Grams = estimate.Grams,
                    Kcal = estimate.Kcal,
                    ProteinG = estimate.ProteinG,
                    FatG = estimate.FatG,
                    CarbG = estimate.CarbG,
                };
                db.DiaryEntries.Add(entry);
                await db.SaveChangesAsync(cancellationToken);

                return Results.Created($"/api/nutrition/diary/{entry.Id}",
                    new DiaryEntryDto(entry.Id, entry.Date, entry.FoodName, entry.Grams, entry.Kcal, entry.ProteinG, entry.FatG, entry.CarbG));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(Permissions.ForTool(Id, "log_meal")))
            .WithName("Nutrition_LogMeal");
    }

    private sealed record DiaryEntryDto(
        Guid Id, DateOnly Date, string FoodName, double Grams, double Kcal, double ProteinG, double FatG, double CarbG);

    private sealed record LogMealRequest(string FoodName, double Grams, DateOnly? Date);
}

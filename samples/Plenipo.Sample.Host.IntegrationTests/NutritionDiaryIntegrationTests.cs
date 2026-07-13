using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The persisted Nutrition food diary: logging a meal computes its macros from the catalog server-side and
/// stores them; the Diary tab reads them back. Tenant-scoped like all module data — the happy-path runs in
/// a dedicated tenant so the stored rows can't perturb other tests.
/// </summary>
[Collection("api")]
public sealed class NutritionDiaryIntegrationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task LoggingAMeal_PersistsComputedMacros_AndReadsBack()
    {
        const string tenant = "nutrition-diary";
        await fixture.EnsureTenantAsync(tenant);
        var client = fixture.ClientForTenant("system_admin", tenant);

        var before = await client.GetFromJsonAsync<JsonElement>("/api/nutrition/diary");
        Assert.Empty(before.EnumerateArray());

        // Log 200 g of chicken breast (165 kcal / 100 g → 330 kcal; 31 g protein / 100 g → 62 g). The client
        // supplies only the food and portion; the server computes and stores the macros from the catalog.
        using (var log = await client.PostAsJsonAsync("/api/nutrition/diary", new { foodName = "Chicken breast", grams = 200 }))
        {
            Assert.Equal(HttpStatusCode.Created, log.StatusCode);
        }

        var after = await client.GetFromJsonAsync<JsonElement>("/api/nutrition/diary");
        var rows = after.EnumerateArray().ToArray();
        Assert.Single(rows);
        Assert.Equal("Chicken breast", rows[0].GetProperty("foodName").GetString());
        Assert.Equal(330d, rows[0].GetProperty("kcal").GetDouble());
        Assert.Equal(62d, rows[0].GetProperty("proteinG").GetDouble());
    }

    [Fact]
    public async Task LoggingAnUnknownFood_IsRejected()
    {
        using var response = await fixture.ClientFor("system_admin")
            .PostAsJsonAsync("/api/nutrition/diary", new { foodName = "zzz-not-a-real-food", grams = 100 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReadingTheDiary_RequiresViewDiaryPermission()
    {
        // A plain user has chat access but not nutrition.diary.view, so the Diary endpoint is refused.
        using var response = await fixture.ClientFor("user").GetAsync("/api/nutrition/diary");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

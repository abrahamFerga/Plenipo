using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Plenipo.AspNetCore.Commerce;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The ProductOffering is authoritative (commercialization phase 9): the host declares what each
/// plan grants; checkout metadata only identifies who bought what. Metadata that CLAIMS more
/// than the plan (extra modules, a bigger budget) gets exactly the plan — the generic contract,
/// adapted per product.
/// </summary>
[Collection("api")]
public sealed class ProductOfferingTests(IntegrationFixture fixture) : IDisposable
{
    private const string Secret = "whsec_offering_test";

    private readonly WebApplicationFactory<Program> _factory = fixture.Factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Commerce:Enabled", "true");
        b.UseSetting("Commerce:WebhookSecret", Secret);
    });

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task LyingCheckoutMetadata_GetsThePlan_NotTheClaims()
    {
        // The metadata claims every module and a huge budget; the sample host's "solo" plan says
        // legal-only, 1 seat, 200k tokens. The plan wins.
        var body = """
            {"id":"evt_offering_1","type":"checkout.session.completed","data":{"object":{
              "id":"cs_off","mode":"subscription","subscription":"sub_offering_1","customer":"cus_off",
              "metadata":{"productId":"the-lawyer","plan":"solo","name":"Greedy Solo","slug":"greedy-solo",
                "adminEmail":"solo@greedy.example","adminSubject":"greedy-solo-admin",
                "modules":"legal,finance,nutrition","monthlyTokenBudget":"999999999"} } } }
            """;
        var bytes = Encoding.UTF8.GetBytes(body);
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("Stripe-Signature", StripeSignature.Compute(bytes, Secret, DateTimeOffset.UtcNow));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        TenantEntitlement? entitlement = null;
        for (var i = 0; i < 30 && entitlement is null; i++)
        {
            await Task.Delay(500);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            entitlement = await db.TenantEntitlements.FirstOrDefaultAsync(
                e => e.SubscriptionRef == "sub_offering_1" && e.Status == EntitlementStatus.Active);
        }

        Assert.NotNull(entitlement);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenant = await db.Tenants.FirstAsync(t => t.Id == entitlement.TenantId);
            Assert.Equal(1, tenant.MaxSeats); // the plan's DefaultSeats, not unlimited

            // The plan's budget, not the metadata's 999,999,999.
            var ai = await db.TenantAiSettings.IgnoreQueryFilters()
                .FirstAsync(a => a.TenantId == tenant.Id);
            Assert.Equal(200_000, ai.MaxMonthlyTokens);
        }

        // The admin sees legal ONLY — the claimed finance/nutrition modules never landed.
        var admin = fixture.Factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Dev-Subject", "greedy-solo-admin");
        admin.DefaultRequestHeaders.Add("X-Dev-Tenant", "greedy-solo");
        var modules = await admin.GetFromJsonAsync<JsonElement>("/api/platform/modules");
        var ids = modules.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToArray();
        Assert.Contains("legal", ids);
        Assert.DoesNotContain("finance", ids);
        Assert.DoesNotContain("nutrition", ids);
    }
}

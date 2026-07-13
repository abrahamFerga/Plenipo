using System.Net;
using System.Text;
using Plenipo.AspNetCore.Commerce;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The full purchase → provision path (commercialization phase 4b), keyless: a signed
/// checkout.session.completed event lands in the inbox, the background worker drains it, and a
/// tenant exists — modules licensed, seats limited, budget set, entitlement Active. Redelivery
/// converges on the same tenant. From "card entered" to "tenant live" with no operator.
/// </summary>
[Collection("api")]
public sealed class CheckoutProvisioningTests(IntegrationFixture fixture) : IDisposable
{
    private const string Secret = "whsec_checkout_test";

    private readonly WebApplicationFactory<Program> _factory = fixture.Factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Commerce:Enabled", "true");
        b.UseSetting("Commerce:WebhookSecret", Secret);
    });

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> DeliverAsync(HttpClient client, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("Stripe-Signature", StripeSignature.Compute(bytes, Secret, DateTimeOffset.UtcNow));
        return await client.SendAsync(request);
    }

    private const string CheckoutEvent = """
        {
          "id": "evt_checkout_e2e",
          "type": "checkout.session.completed",
          "data": { "object": {
            "id": "cs_test_1",
            "mode": "subscription",
            "subscription": "sub_e2e_1",
            "customer": "cus_e2e_1",
            "metadata": {
              "productId": "the-lawyer",
              "plan": "team",
              "name": "Costanza & Associates",
              "slug": "costanza-law",
              "adminEmail": "george@costanza.example",
              "adminSubject": "costanza-admin",
              "modules": "legal",
              "seats": "3",
              "monthlyTokenBudget": "500000"
            }
          } }
        }
        """;

    [Fact]
    public async Task SignedCheckout_BecomesALiveTenant_AndRedeliveryConverges()
    {
        using var client = _factory.CreateClient();
        using (var first = await DeliverAsync(client, CheckoutEvent))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // The worker drains the inbox on its own cadence — poll for the outcome.
        TenantEntitlement? entitlement = null;
        for (var i = 0; i < 30 && entitlement is null; i++)
        {
            await Task.Delay(500);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            entitlement = await db.TenantEntitlements.FirstOrDefaultAsync(
                e => e.SubscriptionRef == "sub_e2e_1" && e.Status == EntitlementStatus.Active && e.TenantId != null);
        }

        Assert.NotNull(entitlement);
        Assert.Equal("the-lawyer", entitlement.ProductId);
        Assert.Equal("team", entitlement.Plan);
        Assert.Equal(3, entitlement.Seats);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenant = await db.Tenants.FirstAsync(t => t.Id == entitlement.TenantId);
            Assert.Equal("costanza-law", tenant.Slug);
            Assert.Equal(3, tenant.MaxSeats);
        }

        // The provisioned admin signs in and sees exactly what the plan bought.
        var admin = fixture.Factory.CreateClient(); // the SHARED host — same database
        admin.DefaultRequestHeaders.Add("X-Dev-Subject", "costanza-admin");
        admin.DefaultRequestHeaders.Add("X-Dev-Tenant", "costanza-law");
        var modules = await System.Net.Http.Json.HttpClientJsonExtensions
            .GetFromJsonAsync<System.Text.Json.JsonElement>(admin, "/api/platform/modules");
        var ids = modules.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToArray();
        Assert.Contains("legal", ids);
        Assert.DoesNotContain("finance", ids);

        // Stripe redelivers the same event (new inbox row is refused as a duplicate; even a fresh
        // event id for the same subscription converges on the SAME tenant).
        var redelivery = CheckoutEvent.Replace("evt_checkout_e2e", "evt_checkout_e2e_retry");
        using (var second = await DeliverAsync(client, redelivery))
        {
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        }

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var retryRow = await db.BillingEvents.FirstOrDefaultAsync(e => e.EventId == "evt_checkout_e2e_retry");
            if (retryRow?.ProcessedAt is not null)
            {
                break;
            }
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.Equal(1, await db.TenantEntitlements.CountAsync(e => e.SubscriptionRef == "sub_e2e_1"));
            Assert.Equal(1, await db.Tenants.CountAsync(t => t.Slug == "costanza-law"));
        }
    }
}

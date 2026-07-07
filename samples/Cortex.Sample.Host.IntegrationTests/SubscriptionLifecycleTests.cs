using System.Net;
using System.Text;
using Cortex.AspNetCore.Commerce;
using Cortex.Core.Platform;
using Cortex.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// The subscription lifecycle after purchase (commercialization phase 4c): payment failure
/// suspends the tenant (kill switch, nothing deleted), payment recovery reactivates it, a seat
/// change resyncs the tenant's limit, and cancellation starts the deprovision grace window.
/// All driven by recorded Stripe-shaped events signed locally.
/// </summary>
[Collection("api")]
public sealed class SubscriptionLifecycleTests(IntegrationFixture fixture) : IDisposable
{
    private const string Secret = "whsec_lifecycle_test";
    private const string Sub = "sub_lifecycle_1";

    private readonly WebApplicationFactory<Program> _factory = fixture.Factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Commerce:Enabled", "true");
        b.UseSetting("Commerce:WebhookSecret", Secret);
        b.UseSetting("Commerce:CancellationGraceDays", "30");
    });

    public void Dispose() => _factory.Dispose();

    private async Task DeliverAsync(string body)
    {
        using var client = _factory.CreateClient();
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("Stripe-Signature", StripeSignature.Compute(bytes, Secret, DateTimeOffset.UtcNow));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<(TenantEntitlement Entitlement, Tenant? Tenant)> WaitForAsync(
        Func<TenantEntitlement, Tenant?, bool> done)
    {
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var entitlement = await db.TenantEntitlements.FirstOrDefaultAsync(e => e.SubscriptionRef == Sub);
            if (entitlement is null)
            {
                continue;
            }

            var tenant = entitlement.TenantId is { } tid ? await db.Tenants.FirstAsync(t => t.Id == tid) : null;
            if (done(entitlement, tenant))
            {
                return (entitlement, tenant);
            }
        }

        Assert.Fail("timed out waiting for the lifecycle transition");
        throw new UnreachableException();
    }

    private static string Event(string id, string type, string dataObject) =>
        $$"""{"id":"{{id}}","type":"{{type}}","data":{"object":{{dataObject}} } }""";

    [Fact]
    public async Task PaymentFailure_Suspends_Recovery_Reactivates_SeatsResync_CancelStartsGrace()
    {
        // Buy: 2-seat team plan → live tenant.
        await DeliverAsync(Event("evt_lc_checkout", "checkout.session.completed", $$"""
            {"id":"cs_lc","mode":"subscription","subscription":"{{Sub}}","customer":"cus_lc","metadata":{
              "productId":"the-lawyer","plan":"team","name":"Lifecycle LLC","slug":"lifecycle-llc",
              "adminEmail":"admin@lifecycle.example","adminSubject":"lifecycle-admin","modules":"legal","seats":"2"} }
            """));
        var (_, tenant) = await WaitForAsync((e, t) => e.Status == EntitlementStatus.Active && t is { IsActive: true });
        Assert.Equal(2, tenant!.MaxSeats);

        // Dunning: a failed invoice suspends the tenant — the kill switch, nothing deleted.
        await DeliverAsync(Event("evt_lc_fail", "invoice.payment_failed", $$"""{"id":"in_1","subscription":"{{Sub}}"}"""));
        await WaitForAsync((e, t) => e.Status == EntitlementStatus.PastDue && t is { IsActive: false });

        // A suspended tenant's users are locked out at request enrichment.
        var user = fixture.Factory.CreateClient();
        user.DefaultRequestHeaders.Add("X-Dev-Subject", "lifecycle-admin");
        user.DefaultRequestHeaders.Add("X-Dev-Tenant", "lifecycle-llc");
        using (var denied = await user.GetAsync("/api/platform/me"))
        {
            Assert.True(denied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        }

        // Recovery: the retried invoice pays → reactivated.
        await DeliverAsync(Event("evt_lc_paid", "invoice.paid", $$"""{"id":"in_1","subscription":"{{Sub}}"}"""));
        await WaitForAsync((e, t) => e.Status == EntitlementStatus.Active && t is { IsActive: true });
        using (var allowed = await user.GetAsync("/api/platform/me"))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        // Self-serve seat upgrade in the Customer Portal → subscription.updated → limit resyncs.
        await DeliverAsync(Event("evt_lc_seats", "customer.subscription.updated", $$"""
            {"id":"{{Sub}}","status":"active","items":{"data":[{"quantity":7}]} }
            """));
        var (entitlement, tenant2) = await WaitForAsync((e, t) => e.Seats == 7 && t is { MaxSeats: 7 });
        Assert.Equal(EntitlementStatus.Active, entitlement.Status);

        // Cancellation: suspended immediately, deprovision scheduled AFTER the grace window.
        await DeliverAsync(Event("evt_lc_cancel", "customer.subscription.deleted", $$"""{"id":"{{Sub}}","status":"canceled"}"""));
        var (canceled, tenant3) = await WaitForAsync((e, t) => e.Status == EntitlementStatus.Canceled);
        Assert.False(tenant3!.IsActive);
        Assert.NotNull(canceled.DeprovisionAfter);
        Assert.True(canceled.DeprovisionAfter > DateTimeOffset.UtcNow.AddDays(29));

        // A stray paid invoice after cancellation must NOT revive the tenant.
        await DeliverAsync(Event("evt_lc_stray", "invoice.paid", $$"""{"id":"in_2","subscription":"{{Sub}}"}"""));
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(500);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var final = await db.TenantEntitlements.FirstAsync(e => e.SubscriptionRef == Sub);
        Assert.Equal(EntitlementStatus.Canceled, final.Status);
        Assert.False((await db.Tenants.FirstAsync(t => t.Id == final.TenantId)).IsActive);
    }

    private sealed class UnreachableException : Exception;
}

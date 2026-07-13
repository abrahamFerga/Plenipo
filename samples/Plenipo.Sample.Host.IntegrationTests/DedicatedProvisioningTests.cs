using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Plenipo.Application.Commerce;
using Plenipo.AspNetCore.Commerce;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The Dedicated tier (commercialization phase 6): a dedicated checkout dispatches the
/// deploy-customer workflow instead of creating a shared-SaaS tenant, and an expired
/// cancellation grace dispatches destroy and marks the entitlement Deprovisioned. The GitHub
/// dispatcher is substituted with a recorder — keyless, no GitHub, no Azure.
/// </summary>
[Collection("api")]
public sealed class DedicatedProvisioningTests : IDisposable
{
    private const string Secret = "whsec_dedicated_test";

    private sealed class RecordingProvisioner : IDedicatedEnvironmentProvisioner
    {
        public ConcurrentQueue<DedicatedEnvironmentRequest> Requests { get; } = new();

        public bool IsConfigured => true;

        public Task DispatchAsync(DedicatedEnvironmentRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            return Task.CompletedTask;
        }
    }

    private readonly RecordingProvisioner _provisioner = new();
    private readonly WebApplicationFactory<Program> _factory;

    public DedicatedProvisioningTests(IntegrationFixture fixture)
    {
        _factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Commerce:Enabled", "true");
            b.UseSetting("Commerce:WebhookSecret", Secret);
            b.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IDedicatedEnvironmentProvisioner>(_provisioner)));
        });
    }

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

    [Fact]
    public async Task DedicatedCheckout_DispatchesApply_NoSharedTenant()
    {
        await DeliverAsync("""
            {"id":"evt_ded_checkout","type":"checkout.session.completed","data":{"object":{
              "id":"cs_ded","mode":"subscription","subscription":"sub_dedicated_1","customer":"cus_ded",
              "metadata":{"productId":"the-lawyer","plan":"dedicated","name":"Big Firm LLP",
                "slug":"big-firm","adminEmail":"it@bigfirm.example","region":"northeurope","size":"medium"} } } }
            """);

        // The worker dispatches apply and leaves the entitlement Provisioning.
        DedicatedEnvironmentRequest? dispatched = null;
        for (var i = 0; i < 30 && dispatched is null; i++)
        {
            await Task.Delay(500);
            _provisioner.Requests.TryDequeue(out dispatched);
        }

        Assert.NotNull(dispatched);
        Assert.Equal("big-firm", dispatched.Customer);
        Assert.Equal("apply", dispatched.Action);
        Assert.Equal("northeurope", dispatched.Region);
        Assert.Equal("medium", dispatched.Size);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entitlement = await db.TenantEntitlements.FirstAsync(e => e.SubscriptionRef == "sub_dedicated_1");
        Assert.Equal(EntitlementStatus.Provisioning, entitlement.Status);
        Assert.Equal("big-firm", entitlement.CustomerSlug);
        Assert.Null(entitlement.TenantId);                                   // no shared-SaaS tenant
        Assert.False(await db.Tenants.AnyAsync(t => t.Slug == "big-firm"));  // really none
    }

    [Fact]
    public async Task ExpiredGrace_DispatchesDestroy_AndDeprovisions()
    {
        // Seed a canceled dedicated entitlement whose grace has already elapsed.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.TenantEntitlements.Add(new TenantEntitlement
            {
                ProductId = "the-lawyer",
                Plan = "dedicated",
                SubscriptionRef = "sub_dedicated_expired",
                CustomerSlug = "gone-firm",
                Status = EntitlementStatus.Canceled,
                DeprovisionAfter = DateTimeOffset.UtcNow.AddMinutes(-5),
            });
            await db.SaveChangesAsync();
        }

        // The sweep runs on the worker's cadence.
        DedicatedEnvironmentRequest? dispatched = null;
        for (var i = 0; i < 30 && dispatched is null; i++)
        {
            await Task.Delay(500);
            _provisioner.Requests.TryDequeue(out dispatched);
        }

        Assert.NotNull(dispatched);
        Assert.Equal("gone-firm", dispatched.Customer);
        Assert.Equal("destroy", dispatched.Action);

        using var verify = _factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entitlement = await verifyDb.TenantEntitlements.FirstAsync(e => e.SubscriptionRef == "sub_dedicated_expired");
        Assert.Equal(EntitlementStatus.Deprovisioned, entitlement.Status);
    }
}

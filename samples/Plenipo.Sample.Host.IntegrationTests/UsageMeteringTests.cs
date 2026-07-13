using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Plenipo.Application.Usage;
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
/// Usage → billing meter (commercialization phase 7): a platform-key tenant's token consumption
/// exports to the meter with an advancing watermark (no double-billing); a BYO-key tenant is
/// never metered. The Stripe meter is substituted with a recorder — keyless.
/// </summary>
[Collection("api")]
public sealed class UsageMeteringTests : IDisposable
{
    private const string Secret = "whsec_metering_test";

    private sealed class RecordingMeter : IBillingMeter
    {
        public ConcurrentQueue<(string CustomerRef, long Value)> Reports { get; } = new();

        public bool IsConfigured => true;

        public Task ReportUsageAsync(
            string customerRef, long value, DateTimeOffset timestamp, string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            Reports.Enqueue((customerRef, value));
            return Task.CompletedTask;
        }
    }

    private readonly RecordingMeter _meter = new();
    private readonly WebApplicationFactory<Program> _factory;

    public UsageMeteringTests(IntegrationFixture fixture)
    {
        _factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Commerce:Enabled", "true");
            b.UseSetting("Commerce:WebhookSecret", Secret);
            b.UseSetting("Commerce:UsageExportSeconds", "1");
            b.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IBillingMeter>(_meter)));
        });
    }

    public void Dispose() => _factory.Dispose();

    private async Task DeliverCheckoutAsync(string sub, string customer, string slug)
    {
        var body = $$"""
            {"id":"evt_meter_{{slug}}","type":"checkout.session.completed","data":{"object":{
              "id":"cs_m","mode":"subscription","subscription":"{{sub}}","customer":"{{customer}}",
              "metadata":{"productId":"the-lawyer","plan":"team","name":"Metered {{slug}}","slug":"{{slug}}",
                "adminEmail":"admin@{{slug}}.example"} } } }
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
    }

    private async Task<Guid> WaitForTenantAsync(string sub)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var e = await db.TenantEntitlements.FirstOrDefaultAsync(
                x => x.SubscriptionRef == sub && x.Status == EntitlementStatus.Active && x.TenantId != null);
            if (e?.TenantId is { } tid)
            {
                return tid;
            }
        }

        throw new InvalidOperationException("tenant never provisioned");
    }

    private async Task AddUsageAsync(Guid tenantId, int tokens)
    {
        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        audit.TokenUsage.Add(new TokenUsageRecord
        {
            TenantId = tenantId,
            ModuleId = "legal",
            Provider = "Mock",
            Model = "mock",
            InputTokens = tokens / 2,
            OutputTokens = tokens - tokens / 2,
            TotalTokens = tokens,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await audit.SaveChangesAsync();
    }

    [Fact]
    public async Task PlatformKeyUsage_ExportsWithAdvancingWatermark()
    {
        await DeliverCheckoutAsync("sub_meter_1", "cus_meter_1", "metered-co");
        var tenantId = await WaitForTenantAsync("sub_meter_1");

        await AddUsageAsync(tenantId, 1234);
        (string CustomerRef, long Value) first = default;
        for (var i = 0; i < 30 && first == default; i++)
        {
            await Task.Delay(500);
            _meter.Reports.TryDequeue(out first);
        }

        Assert.Equal(("cus_meter_1", 1234L), first);

        // Only NEW usage exports next time — the watermark advanced.
        await AddUsageAsync(tenantId, 500);
        (string CustomerRef, long Value) second = default;
        for (var i = 0; i < 30 && second == default; i++)
        {
            await Task.Delay(500);
            _meter.Reports.TryDequeue(out second);
        }

        Assert.Equal(("cus_meter_1", 500L), second);
    }

    [Fact]
    public async Task ByoKeyTenant_IsNeverMetered()
    {
        await DeliverCheckoutAsync("sub_meter_byok", "cus_meter_byok", "byok-co");
        var tenantId = await WaitForTenantAsync("sub_meter_byok");

        // The tenant brings their own provider connection…
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var settings = await db.TenantAiSettings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.TenantId == tenantId);
            if (settings is null)
            {
                db.TenantAiSettings.Add(new TenantAiSettings { TenantId = tenantId, Provider = "OpenAI", Model = "gpt-4o-mini" });
            }
            else
            {
                settings.Provider = "OpenAI";
                settings.Model = "gpt-4o-mini";
            }
            await db.SaveChangesAsync();
        }

        // …so their consumption must never reach the meter.
        await AddUsageAsync(tenantId, 9999);
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(500);
            Assert.DoesNotContain(_meter.Reports, r => r.CustomerRef == "cus_meter_byok");
        }
    }
}

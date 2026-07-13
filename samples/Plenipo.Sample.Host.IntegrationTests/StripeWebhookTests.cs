using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Plenipo.AspNetCore.Commerce;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The billing webhook inbox (commercialization phase 4a): signed events land as BillingEvent
/// rows exactly once; bad signatures bounce; a deployment with commerce off has no surface at
/// all. Keyless — requests are signed locally with a test secret, exactly how Stripe would.
/// </summary>
[Collection("api")]
public sealed class StripeWebhookTests(IntegrationFixture fixture) : IDisposable
{
    private const string Secret = "whsec_integration_test";

    private readonly WebApplicationFactory<Program> _factory = fixture.Factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Commerce:Enabled", "true");
        b.UseSetting("Commerce:WebhookSecret", Secret);
    });

    public void Dispose() => _factory.Dispose();

    private static HttpRequestMessage Signed(string body, string? secretOverride = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("Stripe-Signature",
            StripeSignature.Compute(bytes, secretOverride ?? Secret, DateTimeOffset.UtcNow));
        return request;
    }

    [Fact]
    public async Task SignedEvent_LandsInTheInboxExactlyOnce()
    {
        using var client = _factory.CreateClient();
        const string body = """{"id":"evt_inbox_1","type":"checkout.session.completed","data":{"object":{}}}""";

        using var first = await client.SendAsync(Signed(body));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Stripe redelivers until acknowledged — a duplicate is a 200 and the SAME single row.
        using var second = await client.SendAsync(Signed(body));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var rows = await db.BillingEvents.Where(e => e.EventId == "evt_inbox_1").ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("stripe", row.Provider);
        Assert.Equal("checkout.session.completed", row.Type);
        Assert.Contains("evt_inbox_1", row.PayloadJson);
        Assert.Null(row.ProcessedAt); // pending for the worker (phase 4b)
    }

    [Fact]
    public async Task BadSignature_MissingId_AndMalformedJson_AreRejected()
    {
        using var client = _factory.CreateClient();

        using var wrongKey = await client.SendAsync(
            Signed("""{"id":"evt_bad_sig","type":"x"}""", secretOverride: "whsec_wrong"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongKey.StatusCode);

        using var noId = await client.SendAsync(Signed("""{"type":"x"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);

        using var notJson = await client.SendAsync(Signed("this is not json"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        Assert.False(await db.BillingEvents.AnyAsync(e => e.EventId == "evt_bad_sig"));
    }

    [Fact]
    public async Task CommerceOff_HasNoWebhookSurface()
    {
        // The UNMODIFIED host: no Commerce config → 404, indistinguishable from no endpoint.
        using var client = fixture.Factory.CreateClient();
        using var response = await client.SendAsync(Signed("""{"id":"evt_off","type":"x"}"""));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

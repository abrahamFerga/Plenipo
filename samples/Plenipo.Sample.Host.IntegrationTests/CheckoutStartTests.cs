using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Plenipo.Application.Commerce;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The purchase's front door: POST /api/commerce/checkout validates against the PRODUCT OFFERING
/// and the configured prices, then opens a hosted checkout whose session metadata carries the
/// provisioning identity — set server-side, never by the browser. Stripe is a recorder; keyless.
/// </summary>
[Collection("api")]
public sealed class CheckoutStartTests : IDisposable
{
    private sealed class RecordingCheckout : IStripeCheckout
    {
        public ConcurrentQueue<CheckoutSessionRequest> Sessions { get; } = new();

        public bool IsConfigured => true;

        public Task<string> CreateSessionAsync(CheckoutSessionRequest request, CancellationToken cancellationToken = default)
        {
            Sessions.Enqueue(request);
            return Task.FromResult("https://checkout.stripe.example/session_123");
        }
    }

    private readonly RecordingCheckout _checkout = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly IntegrationFixture _fixture;

    public CheckoutStartTests(IntegrationFixture fixture)
    {
        _fixture = fixture;
        _factory = fixture.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Commerce:Enabled", "true");
            b.UseSetting("Commerce:WebhookSecret", "whsec_start_test");
            b.UseSetting("Commerce:Prices:the-lawyer:team", "price_team_123");
            b.UseSetting("Commerce:CheckoutSuccessUrl", "https://site.example/welcome");
            b.UseSetting("Commerce:CheckoutCancelUrl", "https://site.example/pricing");
            b.ConfigureTestServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IStripeCheckout>(_checkout)));
        });
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ValidPurchase_OpensCheckout_WithServerSideMetadata()
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/commerce/checkout", new
        {
            productId = "the-lawyer",
            plan = "team",
            orgName = "Front Door LLP",
            slug = "Front-Door",     // mixed case in, lowercased server-side
            adminEmail = "buyer@frontdoor.example",
            seats = 4,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://checkout.stripe.example/session_123", payload.GetProperty("url").GetString());

        var session = Assert.Single(_checkout.Sessions);
        Assert.Equal("price_team_123", session.PriceId);
        Assert.Equal(4, session.Quantity);
        Assert.Equal("front-door", session.Metadata["slug"]);
        Assert.Equal("the-lawyer", session.Metadata["productId"]);
        Assert.Equal("team", session.Metadata["plan"]);
        Assert.Equal("https://site.example/welcome", session.SuccessUrl);
    }

    [Fact]
    public async Task InvalidRequests_AreRefusedBeforeStripe()
    {
        using var client = _factory.CreateClient();

        // Unknown plan (not in the offering).
        using var badPlan = await client.PostAsJsonAsync("/api/commerce/checkout", new
        {
            productId = "the-lawyer", plan = "enterprise", orgName = "X", slug = "x-co", adminEmail = "a@x.example",
        });
        Assert.Equal(HttpStatusCode.BadRequest, badPlan.StatusCode);

        // Plan exists but has no configured price.
        using var noPrice = await client.PostAsJsonAsync("/api/commerce/checkout", new
        {
            productId = "the-lawyer", plan = "solo", orgName = "X", slug = "x-co", adminEmail = "a@x.example",
        });
        Assert.Equal(HttpStatusCode.BadRequest, noPrice.StatusCode);

        // Slug already taken by the dev tenant.
        using var taken = await client.PostAsJsonAsync("/api/commerce/checkout", new
        {
            productId = "the-lawyer", plan = "team", orgName = "X", slug = "dev", adminEmail = "a@x.example",
        });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

        Assert.Empty(_checkout.Sessions); // nothing reached the provider
    }

    [Fact]
    public async Task CommerceOff_HasNoCheckoutSurface()
    {
        // The UNMODIFIED host (no Commerce config) exposes nothing at this route.
        using var client = _fixture.Factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/commerce/checkout", new
        {
            productId = "the-lawyer", plan = "team", orgName = "X", slug = "x-off", adminEmail = "a@x.example",
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

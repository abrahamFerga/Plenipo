using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// /api/platform/branding is the runtime seam that lets one prebuilt UI bundle serve every
/// product: anonymous (the name must render before sign-in), sourced from host configuration,
/// defaulting to the platform's own name.
/// </summary>
public class BrandingEndpointTests
{
    private sealed record BrandingResponse(string Name);

    [Fact]
    public async Task Defaults_to_Plenipo_and_requires_no_authentication()
    {
        using var factory = new PlenipoApiFactory();
        using var client = factory.CreateClient(); // no X-Dev-* headers on purpose

        var branding = await client.GetFromJsonAsync<BrandingResponse>("/api/platform/branding");

        Assert.Equal("Plenipo", branding!.Name);
    }

    [Fact]
    public async Task Reflects_the_hosts_configured_product_name()
    {
        using var factory = new BrandedFactory();
        using var client = factory.CreateClient();

        var branding = await client.GetFromJsonAsync<BrandingResponse>("/api/platform/branding");

        Assert.Equal("Acme Finance", branding!.Name);
    }

    private sealed class BrandedFactory : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Branding:ProductName", "Acme Finance");
        }
    }
}

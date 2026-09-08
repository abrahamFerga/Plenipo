using System.Net;
using System.Text.RegularExpressions;
using Plenipo.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// The no-registry UI distribution path: when built SPA assets sit in wwwroot/app, the API host
/// serves them at / with an index.html fallback for client-side deep links — while every reserved
/// platform prefix (/api, /admin, health) keeps resolving to its real endpoint, never the SPA.
/// The shell's scripts carry a per-request nonce, so a product's Content-Security-Policy can admit
/// them by nonce instead of pinning a hash of platform-authored HTML (#197).
/// </summary>
[Collection("api")]
public sealed class DomainUiServingTests(IntegrationFixture fixture) : IDisposable
{
    private static readonly string AssetRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
        "Plenipo.Sample.Host", "wwwroot", "app");

    private WebApplicationFactory<Program>? _factory;

    public void Dispose()
    {
        _factory?.Dispose();
        if (Directory.Exists(AssetRoot))
        {
            Directory.Delete(AssetRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Served_at_root_with_spa_fallback_and_reserved_prefixes_untouched()
    {
        // Stage a built SPA before the host is constructed — the mount decision happens at startup.
        Directory.CreateDirectory(AssetRoot);
        await File.WriteAllTextAsync(Path.Combine(AssetRoot, "index.html"), "<html>casewell-shell</html>");
        await File.WriteAllTextAsync(Path.Combine(AssetRoot, "app.js"), "// bundle");

        _factory = fixture.Factory.WithWebHostBuilder(_ => { });
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", "it-system_admin");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "system_admin");

        // The shell serves at / …
        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("casewell-shell", await root.Content.ReadAsStringAsync());

        // … real files short-circuit …
        Assert.Contains("bundle", await client.GetStringAsync("/app.js"));

        // … deep links fall back to the shell for the client-side router …
        var deepLink = await client.GetAsync("/legal/matters");
        Assert.Contains("casewell-shell", await deepLink.Content.ReadAsStringAsync());

        // … and the platform surface is never shadowed.
        var api = await client.GetAsync("/api/platform/modules");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        Assert.DoesNotContain("casewell-shell", await api.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive")).StatusCode);
    }

    [Fact]
    public async Task Shell_scripts_carry_a_per_request_nonce_that_a_products_policy_can_admit()
    {
        // The real shells carry exactly this: one inline theme initializer plus the module bundle.
        Directory.CreateDirectory(AssetRoot);
        await File.WriteAllTextAsync(Path.Combine(AssetRoot, "index.html"),
            "<html><head><script>document.documentElement.classList.add(\"dark\")</script></head>" +
            "<body><div id=\"root\"></div><script type=\"module\" src=\"/app.js\"></script></body></html>");
        await File.WriteAllTextAsync(Path.Combine(AssetRoot, "app.js"), "// bundle");

        // A product's own CSP middleware, written the way BUILDING_A_PRODUCT.md shows: it runs
        // before the platform serves the shell and asks for the request's nonce.
        _factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, ProductCspStartupFilter>()));
        var client = _factory.CreateClient();

        foreach (var route in new[] { "/", "/index.html", "/legal/matters" })
        {
            using var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

            var html = await response.Content.ReadAsStringAsync();
            var nonces = Regex.Matches(html, "<script nonce=\"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().ToArray();
            var nonce = Assert.Single(nonces);                       // both scripts, one nonce
            Assert.Equal(2, Regex.Matches(html, "<script ").Count);
            Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);

            // The product's header admits exactly the nonce the platform stamped.
            var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains($"'nonce-{nonce}'", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("sha256-", policy, StringComparison.Ordinal);
        }

        // Fresh nonce per response, and static files are untouched.
        var first = Regex.Match(await client.GetStringAsync("/"), "nonce=\"([^\"]+)\"").Groups[1].Value;
        var second = Regex.Match(await client.GetStringAsync("/"), "nonce=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEqual(first, second);
        Assert.Equal("// bundle", await client.GetStringAsync("/app.js"));
    }

    private sealed class ProductCspStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, pipeline) =>
            {
                var nonce = PlenipoCsp.NonceFor(context);
                context.Response.Headers["Content-Security-Policy"] =
                    $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'";
                await pipeline(context);
            });
            next(app);
        };
    }
}

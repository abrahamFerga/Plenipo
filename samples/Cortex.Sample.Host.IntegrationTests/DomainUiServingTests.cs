using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cortex.Sample.Host.IntegrationTests;

/// <summary>
/// The no-registry UI distribution path: when built SPA assets sit in wwwroot/app, the API host
/// serves them at / with an index.html fallback for client-side deep links — while every reserved
/// platform prefix (/api, /admin, health) keeps resolving to its real endpoint, never the SPA.
/// </summary>
[Collection("api")]
public sealed class DomainUiServingTests(IntegrationFixture fixture) : IDisposable
{
    private static readonly string AssetRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
        "Cortex.Sample.Host", "wwwroot", "app");

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
}

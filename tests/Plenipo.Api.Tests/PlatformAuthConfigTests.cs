using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The runtime answer to "how should this client authenticate". Anonymous by necessity: a browser
/// cannot present a token before it knows where to get one, and one prebuilt bundle is published for
/// every deployment (`publish.yml` builds it with an empty `VITE_API_BASE` precisely so), which rules
/// out baking the authority and client id in at build time.
///
/// <para>Because it is anonymous, what it may contain is a security contract, pinned here exactly.</para>
/// </summary>
public sealed class PlatformAuthConfigTests : IDisposable
{
    private readonly List<PlenipoApiFactory> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    private sealed class ConfiguredFactory(IReadOnlyDictionary<string, string> settings) : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        }
    }

    private PlenipoApiFactory Factory(params (string Key, string Value)[] settings)
    {
        var factory = new ConfiguredFactory(settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal));
        _factories.Add(factory);
        return factory;
    }

    [Fact]
    public async Task Is_reachable_without_any_credentials()
    {
        // No X-Dev-* headers at all — the state a browser is in before it has signed in.
        using var client = Factory().CreateClient();

        var response = await client.GetAsync("/api/platform/auth-config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Publishes_exactly_the_public_client_metadata_and_nothing_else()
    {
        // The guard that matters: this endpoint is anonymous, so any property added to it is world-
        // readable. Asserting the exact property set makes adding a fifth one a deliberate act.
        using var client = Factory(
            ("Auth:Authority", "https://idp.example.test/tenant/v2.0"),
            ("Auth:Audience", "api://plenipo-tests"),
            ("Auth:ClientId", "spa-client-id"),
            ("Auth:Scopes", "api://plenipo-tests/.default")).CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/platform/auth-config");

        // "local" joined deliberately with ADR 0003: a boolean deployment-shape flag for the admin
        // console's credential panels — public by definition, secret-free by construction.
        Assert.Equal(
            ["authority", "clientId", "local", "mode", "scopes"],
            body.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal("oidc", body.GetProperty("mode").GetString());
        Assert.Equal("https://idp.example.test/tenant/v2.0", body.GetProperty("authority").GetString());
        Assert.Equal("spa-client-id", body.GetProperty("clientId").GetString());

        // Nothing that could be a credential, however the DTO is later extended.
        var raw = (await client.GetAsync("/api/platform/auth-config")).Content.ReadAsStringAsync().Result;
        foreach (var forbidden in new[] { "secret", "password", "key", "audience" })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Reports_dev_mode_when_no_authority_is_configured()
    {
        // Without this the shell would redirect a local developer to an empty authority URL.
        using var client = Factory().CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/platform/auth-config");

        Assert.Equal("dev", body.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("authority").ValueKind);
    }

    [Fact]
    public async Task A_missing_client_id_still_reports_oidc_mode()
    {
        // Auth:ClientId is deliberately NOT part of the startup fail-fast — extending it would crash
        // every existing API-only deployment on upgrade with no config change of its own. So the mode
        // must stay honest and let the client say what is missing.
        using var client = Factory(
            ("Auth:Authority", "https://idp.example.test/tenant/v2.0"),
            ("Auth:Audience", "api://plenipo-tests")).CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/platform/auth-config");

        Assert.Equal("oidc", body.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("clientId").ValueKind);
    }

    [Fact]
    public async Task Local_mode_publishes_the_host_itself_as_the_authority()
    {
        // ADR 0003: the shell's existing PKCE flow must work unchanged, so the mode stays "oidc",
        // the authority is the origin the browser provably reached, and offline_access makes
        // refresh tokens flow. The built-in client id is public metadata like any other.
        using var client = Factory(("Auth:Mode", "Local")).CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/platform/auth-config");

        Assert.Equal("oidc", body.GetProperty("mode").GetString());
        Assert.Equal("http://localhost", body.GetProperty("authority").GetString());
        Assert.Equal("plenipo-web", body.GetProperty("clientId").GetString());
        Assert.Equal("offline_access", body.GetProperty("scopes").GetString());
        Assert.True(body.GetProperty("local").GetBoolean());
    }

    [Fact]
    public async Task Only_GET_is_accepted()
    {
        using var client = Factory().CreateClient();

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var response = await client.SendAsync(new HttpRequestMessage(method, "/api/platform/auth-config"));
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }
}

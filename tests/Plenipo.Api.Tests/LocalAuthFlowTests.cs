using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.LocalAuth;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// Auth:Mode=Local (ADR 0003), driven exactly as a browser drives it: authorize → login page →
/// (forced password change) → code → token → API call — plus the failure paths that make an issuer
/// trustworthy: lockout, refresh rotation, stamp revocation, foreign redirect hosts, and token-kind
/// confusion. Everything runs over the in-process TestServer with the client's cookie jar doing what
/// a browser's would.
/// </summary>
public sealed class LocalAuthFlowTests : IDisposable
{
    private const string Password = "initial-password-123";
    private readonly List<PlenipoApiFactory> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    private sealed class LocalModeFactory(IReadOnlyDictionary<string, string> extra) : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Mode", "Local");
            // TestServer speaks plain HTTP, same as a LAN mini PC — the documented opt-out.
            builder.UseSetting("Auth:RequireHttpsMetadata", "false");
            foreach (var (key, value) in extra)
            {
                builder.UseSetting(key, value);
            }
        }
    }

    private PlenipoApiFactory Local(params (string Key, string Value)[] extra)
    {
        var factory = new LocalModeFactory(extra.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal));
        _factories.Add(factory);
        return factory;
    }

    private static HttpClient Client(PlenipoApiFactory factory) => factory.CreateClient(
        new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A user with a local credential in the dev tenant; returns their platform row.</summary>
    private static async Task<User> SeedUserAsync(
        PlenipoApiFactory factory, string email, bool mustChange = true, string role = "tenant_admin")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = await db.Tenants.FirstAsync(t => t.Slug == "dev");
        var user = new User
        {
            TenantId = tenant.Id,
            Subject = $"local|{Guid.CreateVersion7():N}",
            Email = email,
            DisplayName = "Local Tester",
        };
        user.Roles.Add(new UserRole { TenantId = tenant.Id, UserId = user.Id, Role = role });
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var credentials = scope.ServiceProvider.GetRequiredService<LocalCredentialService>();
        var (credential, _, error) = await credentials.CreateAsync(user, Password, null, default);
        Assert.Null(error);
        if (!mustChange)
        {
            credential!.MustChangePassword = false;
            await db.SaveChangesAsync();
        }

        return user;
    }

    // ── The happy path, exactly as a browser walks it ────────────────────────

    [Fact]
    public async Task Full_code_flow_with_forced_password_change_yields_a_working_access_token()
    {
        var factory = Local();
        var client = Client(factory);
        var user = await SeedUserAsync(factory, "ada@local.test", mustChange: true);
        var (verifier, challenge) = Pkce();

        // Discovery is what the SPA's PKCE client fetches first.
        var discovery = await client.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        Assert.EndsWith("/connect/authorize", discovery.GetProperty("authorization_endpoint").GetString());
        Assert.EndsWith("/connect/token", discovery.GetProperty("token_endpoint").GetString());

        // authorize, unauthenticated → the login page.
        var authorizeUrl = AuthorizeUrl(challenge);
        var toLogin = await client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Found, toLogin.StatusCode);
        // The cookie handler emits an absolute Location; the part under test is path + ReturnUrl.
        var loginUrl = toLogin.Headers.Location!.PathAndQuery;
        Assert.StartsWith("/auth/login", loginUrl, StringComparison.Ordinal);

        var loginPage = await client.GetStringAsync(loginUrl);
        var csrf = ExtractInput(loginPage, "csrf");
        var returnUrl = ExtractInput(loginPage, "returnUrl");

        // Correct password on a must-change credential → the change form, not a session.
        var changePage = await client.PostAsync("/auth/login", Form(new()
        {
            ["email"] = "ada@local.test",
            ["password"] = Password,
            ["csrf"] = csrf,
            ["returnUrl"] = returnUrl,
        }));
        var changeHtml = await changePage.Content.ReadAsStringAsync();
        Assert.Contains("new password", changeHtml, StringComparison.OrdinalIgnoreCase);

        var done = await client.PostAsync("/auth/login/change", Form(new()
        {
            ["password"] = "a-brand-new-password",
            ["confirm"] = "a-brand-new-password",
            ["csrf"] = ExtractInput(changeHtml, "csrf"),
            ["step"] = ExtractInput(changeHtml, "step"),
            ["returnUrl"] = ExtractInput(changeHtml, "returnUrl"),
        }));
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);

        // Back to authorize, now with the session cookie → the code lands on the callback.
        var withSession = await client.GetAsync(done.Headers.Location);
        Assert.Equal(HttpStatusCode.Found, withSession.StatusCode);
        var callback = withSession.Headers.Location!;
        Assert.Equal("http", callback.Scheme);
        Assert.Equal("/signin-callback", callback.AbsolutePath);
        var query = QueryHelpers.ParseQuery(callback.Query);
        Assert.Equal("st123", query["state"]);
        var code = query["code"].ToString();
        Assert.NotEmpty(code);

        // The exchange the SPA performs — public client, PKCE, no secret.
        var tokens = await ExchangeAsync(client, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "http://localhost/signin-callback",
            ["client_id"] = "plenipo-web",
            ["code_verifier"] = verifier,
        });
        var accessToken = tokens.GetProperty("access_token").GetString()!;
        Assert.NotEmpty(tokens.GetProperty("refresh_token").GetString()!);

        // The minted token drives the SAME API surface external OIDC would.
        using var api = Client(factory);
        api.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        var me = await api.GetFromJsonAsync<JsonElement>("/api/platform/me");
        Assert.Equal(user.Subject, me.GetProperty("subject").GetString());
        Assert.True(me.GetProperty("tenantResolved").GetBoolean());
    }

    [Fact]
    public async Task Refresh_rotates_and_a_password_reset_revokes_outstanding_refresh_tokens()
    {
        var factory = Local();
        var client = Client(factory);
        var user = await SeedUserAsync(factory, "grace@local.test", mustChange: false);

        var first = await SignInForTokensAsync(client, "grace@local.test", Password);
        var refresh = first.GetProperty("refresh_token").GetString()!;

        var second = await ExchangeAsync(client, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = "plenipo-web",
        });
        var rotated = second.GetProperty("refresh_token").GetString()!;
        Assert.NotEqual(refresh, rotated);

        // Admin resets the password → the stamp rotates → the live refresh token dies.
        using (var scope = factory.Services.CreateScope())
        {
            var credentials = scope.ServiceProvider.GetRequiredService<LocalCredentialService>();
            var credential = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
                .LocalCredentials.IgnoreQueryFilters().FirstAsync(c => c.UserId == user.Id);
            await credentials.ResetToTemporaryAsync(credential, null, default);
        }

        var refused = await client.PostAsync("/connect/token", Form(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = rotated,
            ["client_id"] = "plenipo-web",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("invalid_grant", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // ── The failure paths that make an issuer trustworthy ────────────────────

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_with_an_identical_error_shape()
    {
        var factory = Local();
        var client = Client(factory);
        await SeedUserAsync(factory, "locked@local.test", mustChange: false);

        var loginPage = await client.GetStringAsync("/auth/login");
        var csrf = ExtractInput(loginPage, "csrf");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await client.PostAsync("/auth/login", Form(new()
            {
                ["email"] = "locked@local.test",
                ["password"] = "definitely-wrong",
                ["csrf"] = csrf,
                ["returnUrl"] = "/",
            }));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // Now even the RIGHT password is refused until the lockout lapses.
        var lockedOut = await client.PostAsync("/auth/login", Form(new()
        {
            ["email"] = "locked@local.test",
            ["password"] = Password,
            ["csrf"] = csrf,
            ["returnUrl"] = "/",
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, lockedOut.StatusCode);
        Assert.Contains("Try again in", await lockedOut.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_foreign_redirect_host_is_refused_outright()
    {
        var factory = Local();
        var client = Client(factory);
        await SeedUserAsync(factory, "evil@local.test", mustChange: false);
        var (_, challenge) = Pkce();

        // Same path, wrong host: the same-host rule must not be a path-only rule.
        var response = await client.GetAsync(
            "/connect/authorize?response_type=code&client_id=plenipo-web" +
            "&redirect_uri=" + Uri.EscapeDataString("http://evil.example/signin-callback") +
            "&scope=openid&state=x&code_challenge=" + challenge + "&code_challenge_method=S256");

        // OpenIddict answers the CLIENT (an error page), never the attacker's redirect target.
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task An_id_token_is_not_an_access_token()
    {
        var factory = Local();
        var client = Client(factory);
        await SeedUserAsync(factory, "confused@local.test", mustChange: false);

        var tokens = await SignInForTokensAsync(client, "confused@local.test", Password);
        var idToken = tokens.GetProperty("id_token").GetString()!;

        using var api = Client(factory);
        api.DefaultRequestHeaders.Authorization = new("Bearer", idToken);
        var response = await api.GetAsync("/api/platform/me");

        // Different audience AND different typ — either alone rejects it; together it's structural.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Local_credential_endpoints_answer_409_outside_local_mode()
    {
        var factory = new PlenipoApiFactory(); // plain dev-mode host, no Auth:Mode
        _factories.Add(factory);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/api/auth/local-status")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.GetAsync("/api/admin/users/local/")).StatusCode);
    }

    [Fact]
    public void Local_mode_with_an_external_authority_refuses_to_start()
    {
        var factory = Local(("Auth:Authority", "https://idp.example.test"), ("Auth:Audience", "api://x"));
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("mutually exclusive", ex.Message, StringComparison.Ordinal);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static string AuthorizeUrl(string challenge) =>
        "/connect/authorize?response_type=code&client_id=plenipo-web" +
        "&redirect_uri=" + Uri.EscapeDataString("http://localhost/signin-callback") +
        "&scope=" + Uri.EscapeDataString("openid profile email offline_access") +
        "&state=st123&code_challenge=" + challenge + "&code_challenge_method=S256";

    /// <summary>Password → (forced-change if present) → code → tokens, for tests past the first.</summary>
    private static async Task<JsonElement> SignInForTokensAsync(HttpClient client, string email, string password)
    {
        var (verifier, challenge) = Pkce();
        var toLogin = await client.GetAsync(AuthorizeUrl(challenge));
        var loginPage = await client.GetStringAsync(toLogin.Headers.Location!.ToString());

        var afterLogin = await client.PostAsync("/auth/login", Form(new()
        {
            ["email"] = email,
            ["password"] = password,
            ["csrf"] = ExtractInput(loginPage, "csrf"),
            ["returnUrl"] = ExtractInput(loginPage, "returnUrl"),
        }));
        Assert.Equal(HttpStatusCode.Redirect, afterLogin.StatusCode);

        var withSession = await client.GetAsync(afterLogin.Headers.Location);
        var code = QueryHelpers.ParseQuery(withSession.Headers.Location!.Query)["code"].ToString();

        return await ExchangeAsync(client, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = "http://localhost/signin-callback",
            ["client_id"] = "plenipo-web",
            ["code_verifier"] = verifier,
        });
    }

    private static async Task<JsonElement> ExchangeAsync(HttpClient client, Dictionary<string, string> form)
    {
        var response = await client.PostAsync("/connect/token", Form(form));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"token endpoint answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new(fields);

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string ExtractInput(string html, string name)
    {
        var match = Regex.Match(html, $"""name="{name}" value="([^"]*)" """.TrimEnd());
        Assert.True(match.Success, $"no hidden input '{name}' in page:\n{html[..Math.Min(html.Length, 500)]}");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}

using System.Security.Claims;
using System.Text.Encodings.Web;
using Plenipo.AspNetCore.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Unit-level guards on the Development-only <see cref="DevAuthenticationHandler"/> (no server/Docker — not
/// in the "api" collection): the X-Dev-* headers become a principal, and X-Dev-Roles is parsed robustly
/// (comma-separated, trimmed, empties dropped) since that mapping drives the dev user's effective permissions.
/// <para>
/// The query-string tests cover the hub transport: a browser's WebSocket handshake cannot set headers, so
/// without a query fallback every <c>/hubs</c> turn in Development authenticates as the handler's defaults —
/// tenant <c>dev</c>, roles <c>system_admin</c> — regardless of who the caller actually is.
/// </para>
/// </summary>
public sealed class DevAuthenticationHandlerTests
{
    private sealed class SchemeOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => new();
        public AuthenticationSchemeOptions Get(string? name) => new();
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private static async Task<ClaimsPrincipal> AuthenticateAsync(Action<IHeaderDictionary>? setHeaders = null) =>
        await AuthenticateRequestAsync(context => setHeaders?.Invoke(context.Request.Headers));

    private static async Task<ClaimsPrincipal> AuthenticateRequestAsync(Action<HttpContext> configure)
    {
        var handler = new DevAuthenticationHandler(new SchemeOptionsMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default);
        var context = new DefaultHttpContext();
        configure(context);
        await handler.InitializeAsync(
            new AuthenticationScheme(DevAuthenticationHandler.SchemeName, null, typeof(DevAuthenticationHandler)),
            context);

        var result = await handler.AuthenticateAsync();
        Assert.True(result.Succeeded);
        return result.Principal!;
    }

    /// <summary>Builds a request at <paramref name="path"/> carrying <paramref name="query"/>.</summary>
    private static Action<HttpContext> Request(string path, params (string Key, string Value)[] query) =>
        context =>
        {
            context.Request.Path = path;
            context.Request.QueryString = QueryString.Create(
                query.Select(q => new KeyValuePair<string, string?>(q.Key, q.Value)));
        };

    [Fact]
    public async Task ParsesMultipleRoles_TrimmingAndDroppingEmpties()
    {
        var principal = await AuthenticateAsync(h => h["X-Dev-Roles"] = "system_admin, user,,guest");
        Assert.Equal(
            new[] { "system_admin", "user", "guest" },
            principal.FindAll("roles").Select(c => c.Value));
    }

    [Fact]
    public async Task DefaultsToSystemAdminDevUser_WhenNoHeadersPresent()
    {
        var principal = await AuthenticateAsync();
        Assert.Equal("dev-user", principal.FindFirstValue("sub"));
        Assert.Equal("dev", principal.FindFirstValue("tenant"));
        Assert.Equal(new[] { "system_admin" }, principal.FindAll("roles").Select(c => c.Value));
    }

    [Fact]
    public async Task ResolvesIdentityFromQuery_OnHubPaths()
    {
        var principal = await AuthenticateRequestAsync(Request(
            "/hubs/agent",
            ("X-Dev-Subject", "it-analyst"),
            ("X-Dev-Tenant", "acme"),
            ("X-Dev-Roles", "analyst,reader")));

        Assert.Equal("it-analyst", principal.FindFirstValue("sub"));
        Assert.Equal("it-analyst", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("acme", principal.FindFirstValue("tenant"));
        Assert.Equal(new[] { "analyst", "reader" }, principal.FindAll("roles").Select(c => c.Value));
    }

    /// <summary>
    /// The hub-path restriction is the whole safety argument, mirroring the JwtBearer rule in
    /// <c>AuthSetup</c>: a query string reaches browser history, proxy logs and error reports, so the REST
    /// surface — which can carry headers perfectly well — must keep ignoring it.
    /// </summary>
    [Theory]
    [InlineData("/api/chat/approvals")]
    [InlineData("/")]
    [InlineData("/hubsimposter/agent")]
    public async Task IgnoresQueryIdentity_OffHubPaths(string path)
    {
        var principal = await AuthenticateRequestAsync(Request(
            path,
            ("X-Dev-Subject", "escalated"),
            ("X-Dev-Tenant", "victim"),
            ("X-Dev-Roles", "system_admin")));

        Assert.Equal("dev-user", principal.FindFirstValue("sub"));
        Assert.Equal("dev", principal.FindFirstValue("tenant"));
    }

    [Fact]
    public async Task HeaderWinsOverQuery_WhenBothPresentOnAHubPath()
    {
        var principal = await AuthenticateRequestAsync(context =>
        {
            Request("/hubs/agent", ("X-Dev-Subject", "from-query"), ("X-Dev-Roles", "reader"))(context);
            context.Request.Headers["X-Dev-Subject"] = "from-header";
            context.Request.Headers["X-Dev-Roles"] = "analyst";
        });

        Assert.Equal("from-header", principal.FindFirstValue("sub"));
        Assert.Equal(new[] { "analyst" }, principal.FindAll("roles").Select(c => c.Value));
    }

    /// <summary>
    /// Same asymmetry the header path already encodes: an ABSENT roles key means "dev convenience,
    /// default to system_admin", a PRESENT-but-empty one means an explicitly role-less principal. A query
    /// fallback that collapsed the two would silently hand system_admin to a caller asking for nothing.
    /// </summary>
    [Fact]
    public async Task PresentButEmptyRolesQuery_YieldsARolelessPrincipal()
    {
        var principal = await AuthenticateRequestAsync(Request(
            "/hubs/agent",
            ("X-Dev-Subject", "unscoped"),
            ("X-Dev-Roles", "")));

        Assert.Equal("unscoped", principal.FindFirstValue("sub"));
        Assert.Empty(principal.FindAll("roles"));
    }
}

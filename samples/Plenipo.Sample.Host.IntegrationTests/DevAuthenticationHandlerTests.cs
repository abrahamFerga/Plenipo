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
/// </summary>
public sealed class DevAuthenticationHandlerTests
{
    private sealed class SchemeOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => new();
        public AuthenticationSchemeOptions Get(string? name) => new();
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private static async Task<ClaimsPrincipal> AuthenticateAsync(Action<IHeaderDictionary>? setHeaders = null)
    {
        var handler = new DevAuthenticationHandler(new SchemeOptionsMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default);
        var context = new DefaultHttpContext();
        setHeaders?.Invoke(context.Request.Headers);
        await handler.InitializeAsync(
            new AuthenticationScheme(DevAuthenticationHandler.SchemeName, null, typeof(DevAuthenticationHandler)),
            context);

        var result = await handler.AuthenticateAsync();
        Assert.True(result.Succeeded);
        return result.Principal!;
    }

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
}

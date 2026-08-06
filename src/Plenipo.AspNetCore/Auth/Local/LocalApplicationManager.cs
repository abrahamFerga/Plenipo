using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OpenIddict.EntityFrameworkCore.Models;

namespace Plenipo.AspNetCore.Auth.Local;

/// <summary>
/// The embedded issuer's application manager, differing from stock OpenIddict in exactly one policy:
/// redirect URIs are validated <b>same-host-by-path</b> instead of against a static registered list.
///
/// <para>Why: the SPA the issuer serves is the host's OWN bundle, and an on-prem host is legitimately
/// reached under several names at once — <c>http://192.168.1.20:8080</c>, <c>http://plenipo.local</c>,
/// a LAN hostname — none knowable at registration time. A static list would make sign-in work by IP
/// and fail by hostname. So a redirect is accepted when its scheme+host+port equal the host the
/// browser is talking to right now and its path is exactly one of the shell callback paths
/// (no wildcards, no foreign hosts, no query/fragment).</para>
///
/// <para>Why that is sound: the "current host" here is the Host the USER'S OWN browser sent on the
/// authorization request — the same value every absolute link the platform emits already trusts
/// (invite links, connector OAuth). An attacker who lures a victim to the authorize URL cannot make
/// the victim's browser send an attacker-controlled Host to THIS server, so they cannot steer the
/// code anywhere but the deployment the victim is actually using. Registered URIs (the dev-server
/// origins the initializer seeds) still validate first, so nothing stock breaks.</para>
/// </summary>
internal sealed class LocalApplicationManager(
    IOpenIddictApplicationCache<OpenIddictEntityFrameworkCoreApplication> cache,
    ILogger<OpenIddictApplicationManager<OpenIddictEntityFrameworkCoreApplication>> logger,
    IOptionsMonitor<OpenIddictCoreOptions> options,
    IOpenIddictApplicationStore<OpenIddictEntityFrameworkCoreApplication> store,
    IHttpContextAccessor httpContextAccessor)
    : OpenIddictApplicationManager<OpenIddictEntityFrameworkCoreApplication>(cache, logger, options, store)
{
    public override async ValueTask<bool> ValidateRedirectUriAsync(
        OpenIddictEntityFrameworkCoreApplication application, string uri, CancellationToken cancellationToken = default)
    {
        if (await base.ValidateRedirectUriAsync(application, uri, cancellationToken))
        {
            return true;
        }

        return MatchesCurrentHost(uri, LocalAuthDefaults.CallbackPaths);
    }

    public override async ValueTask<bool> ValidatePostLogoutRedirectUriAsync(
        OpenIddictEntityFrameworkCoreApplication application, string uri, CancellationToken cancellationToken = default)
    {
        if (await base.ValidatePostLogoutRedirectUriAsync(application, uri, cancellationToken))
        {
            return true;
        }

        // After sign-out the shell returns to its root; the callback paths cover an admin-scoped shell.
        return MatchesCurrentHost(uri, ["/", .. LocalAuthDefaults.CallbackPaths]);
    }

    private bool MatchesCurrentHost(string uri, IReadOnlyList<string> allowedPaths)
    {
        var request = httpContextAccessor.HttpContext?.Request;
        if (request is null || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        return string.Equals(parsed.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(parsed.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
               && string.IsNullOrEmpty(parsed.Query)
               && string.IsNullOrEmpty(parsed.Fragment)
               && allowedPaths.Contains(parsed.AbsolutePath, StringComparer.Ordinal);
    }
}

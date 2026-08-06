using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Plenipo.AspNetCore.RateLimiting;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.LocalAuth;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Plenipo.AspNetCore.Auth.Local;

/// <summary>
/// The embedded issuer's HTTP surface (ADR 0003): the OpenIddict passthrough endpoints
/// (authorize / token / end-session) and the server-rendered login flow (password → TOTP → forced
/// change). Everything here is anonymous by design — it is what CREATES sessions — and the login
/// POSTs sit behind the auth rate-limit policy; account lockout is enforced per credential besides.
/// The multi-step login carries its state in a Data-Protection-signed step token, so the server
/// stays stateless until the final cookie sign-in.
/// </summary>
public static class LocalAuthEndpoints
{
    private const string CsrfCookieName = "plenipo.auth.csrf";
    private const string StepProtectorPurpose = "Plenipo.LocalAuth.LoginStep";
    private static readonly TimeSpan StepLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Wrong-email and wrong-password answer identically; enumeration learns nothing.</summary>
    private const string GenericSignInError = "That email or password is not right.";

    public static void MapPlenipoLocalAuth(this IEndpointRouteBuilder app)
    {
        app.MapMethods(LocalAuthDefaults.AuthorizeEndpoint, [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync)
            .WithName("LocalAuth_Authorize");
        app.MapPost(LocalAuthDefaults.TokenEndpoint, ExchangeAsync)
            .RequireRateLimiting(RateLimitingSetup.AuthPolicy)
            .WithName("LocalAuth_Token");
        // Cast to Delegate: a (HttpContext) => Task<IResult> method group would otherwise convert to
        // RequestDelegate, silently discarding the IResult (ASP0016).
        app.MapMethods(LocalAuthDefaults.EndSessionEndpoint, [HttpMethods.Get, HttpMethods.Post], (Delegate)EndSessionAsync)
            .WithName("LocalAuth_EndSession");

        app.MapGet(LocalAuthDefaults.LoginPath, LoginPageAsync).WithName("LocalAuth_LoginPage");
        app.MapPost(LocalAuthDefaults.LoginPath, SubmitPasswordAsync)
            .RequireRateLimiting(RateLimitingSetup.AuthPolicy)
            .WithName("LocalAuth_LoginSubmit");
        app.MapPost($"{LocalAuthDefaults.LoginPath}/totp", SubmitTotpAsync)
            .RequireRateLimiting(RateLimitingSetup.AuthPolicy)
            .WithName("LocalAuth_TotpSubmit");
        app.MapPost($"{LocalAuthDefaults.LoginPath}/change", SubmitPasswordChangeAsync)
            .RequireRateLimiting(RateLimitingSetup.AuthPolicy)
            .WithName("LocalAuth_ChangeSubmit");
    }

    // ── /connect/authorize ───────────────────────────────────────────────────

    private static async Task<IResult> AuthorizeAsync(
        HttpContext context, PlatformDbContext db, CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        var session = await context.AuthenticateAsync(LocalAuthDefaults.CookieScheme);
        if (session is not { Succeeded: true, Principal: { } cookiePrincipal }
            || !Guid.TryParse(cookiePrincipal.FindFirstValue(LocalAuthDefaults.UserIdClaim), out var userId))
        {
            return ChallengeOrLoginRequired(context, request);
        }

        // The cookie says who signed in; the LIVE rows say whether that still stands. A deactivated
        // user, a rotated stamp (password reset), or a freshly-forced change all end the session here
        // rather than minting one more code on stale authority.
        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var credential = await db.LocalCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        var tenant = user is null
            ? null
            : await db.Tenants.FirstOrDefaultAsync(t => t.Id == user.TenantId, cancellationToken);
        var sessionStamp = cookiePrincipal.FindFirstValue(LocalAuthDefaults.StampClaim);

        if (user is not { IsActive: true } || tenant is not { IsActive: true } || credential is null
            || credential.MustChangePassword
            || LocalCredentialService.IsLockedOut(credential)
            || !string.Equals(sessionStamp, credential.SecurityStamp, StringComparison.Ordinal))
        {
            await context.SignOutAsync(LocalAuthDefaults.CookieScheme);
            return ChallengeOrLoginRequired(context, request);
        }

        var identity = new ClaimsIdentity(
            authenticationType: "Local", nameType: Claims.Name, roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, user.Subject);
        identity.SetClaim(Claims.Name, user.DisplayName ?? user.Email);
        identity.SetClaim(Claims.Email, user.Email);
        // The claim RequestEnricher resolves the tenant from — same wire shape an external IdP uses.
        identity.SetClaim("tenant", tenant.Slug);
        identity.SetClaim(LocalAuthDefaults.UserIdClaim, user.Id.ToString("N"));
        identity.SetClaim(LocalAuthDefaults.StampClaim, credential.SecurityStamp);
        identity.SetClaims(Claims.AuthenticationMethodReference,
            [.. cookiePrincipal.FindAll(Claims.AuthenticationMethodReference).Select(c => c.Value)]);

        identity.SetScopes(request.GetScopes());
        identity.SetResources(LocalAuthDefaults.Audience);
        identity.SetDestinations(GetDestinations);

        return Results.SignIn(
            new ClaimsPrincipal(identity), properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult ChallengeOrLoginRequired(HttpContext context, OpenIddictRequest request)
    {
        // prompt=none is a machine asking "is there a session?" — answer in protocol, never in HTML.
        var prompts = (request.Prompt ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (prompts.Contains(PromptValues.None, StringComparer.Ordinal))
        {
            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.LoginRequired,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "Sign-in is required.",
                }),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = returnUrl }, [LocalAuthDefaults.CookieScheme]);
    }

    private static IEnumerable<string> GetDestinations(Claim claim) => claim.Type switch
    {
        // Name/email/tenant also travel in the id_token for the shell's benefit; the internal
        // bookkeeping claims stay access-token-only. Nothing here is a secret — but the stamp and
        // uid are protocol plumbing no client should come to depend on.
        Claims.Name or Claims.Email or "tenant" or Claims.AuthenticationMethodReference =>
            [Destinations.AccessToken, Destinations.IdentityToken],
        _ => [Destinations.AccessToken],
    };

    // ── /connect/token ───────────────────────────────────────────────────────

    private static async Task<IResult> ExchangeAsync(
        HttpContext context, PlatformDbContext db, CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict server request cannot be retrieved.");

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            return Forbid(Errors.UnsupportedGrantType, "Only authorization_code and refresh_token are supported.");
        }

        // OpenIddict has already validated the code/refresh token (single use, PKCE, client, expiry)
        // and reconstructed the principal minted at authorize time.
        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        if (result is not { Succeeded: true, Principal: { } principal }
            || !Guid.TryParse(principal.GetClaim(LocalAuthDefaults.UserIdClaim), out var userId))
        {
            return Forbid(Errors.InvalidGrant, "The token is no longer valid.");
        }

        // Re-check live rows so a refresh token cannot outlive a deactivation or password reset:
        // the stamp in the token must still be THE stamp.
        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var credential = await db.LocalCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        var tenantActive = user is not null
            && await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.IsActive, cancellationToken);

        if (user is not { IsActive: true } || !tenantActive || credential is null
            || credential.MustChangePassword
            || !string.Equals(
                principal.GetClaim(LocalAuthDefaults.StampClaim), credential.SecurityStamp, StringComparison.Ordinal))
        {
            return Forbid(Errors.InvalidGrant, "The session is no longer valid. Sign in again.");
        }

        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        static IResult Forbid(string error, string description) => Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    // ── /connect/logout ──────────────────────────────────────────────────────

    private static async Task<IResult> EndSessionAsync(HttpContext context)
    {
        await context.SignOutAsync(LocalAuthDefaults.CookieScheme);

        // OpenIddict validates any post_logout_redirect_uri (LocalApplicationManager's same-host
        // rule) and redirects there; the RedirectUri below is only the fallback when none was sent.
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }

    // ── The login flow ───────────────────────────────────────────────────────

    private static async Task<IResult> LoginPageAsync(HttpContext context, IConfiguration configuration)
    {
        var returnUrl = SafeReturnUrl(context.Request.Query["ReturnUrl"]);
        var session = await context.AuthenticateAsync(LocalAuthDefaults.CookieScheme);
        if (session.Succeeded)
        {
            return Results.Redirect(returnUrl);
        }

        return Html(LocalLoginPages.Login(ProductName(configuration), returnUrl, IssueCsrf(context), error: null));
    }

    private static async Task<IResult> SubmitPasswordAsync(
        HttpContext context, PlatformDbContext db, LocalCredentialService credentials,
        IDataProtectionProvider dataProtection, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var product = ProductName(configuration);
        var returnUrl = SafeReturnUrl(form["returnUrl"]);

        if (!CsrfValid(context, form["csrf"]))
        {
            return Html(LocalLoginPages.Login(product, returnUrl, IssueCsrf(context),
                "The form expired — please try again."), StatusCodes.Status400BadRequest);
        }

        var csrf = (string)form["csrf"]!;
        var email = (string?)form["email"] ?? "";
        var password = (string?)form["password"] ?? "";

        var credential = await credentials.FindForSignInAsync(email, cancellationToken);
        if (credential is null)
        {
            // Burn comparable time on a dummy verification so "no such account" and "wrong password"
            // are indistinguishable by response AND by clock.
            await credentials.VerifyPasswordAsync(DummyCredential.Value, password, cancellationToken);
            return Html(LocalLoginPages.Login(product, returnUrl, csrf, GenericSignInError),
                StatusCodes.Status401Unauthorized);
        }

        if (LocalCredentialService.IsLockedOut(credential))
        {
            return Html(LocalLoginPages.Login(product, returnUrl, csrf, LockedMessage(credential)),
                StatusCodes.Status401Unauthorized);
        }

        if (!await credentials.VerifyPasswordAsync(credential, password, cancellationToken))
        {
            await credentials.RegisterFailureAsync(credential, "wrong password", ClientIp(context), cancellationToken);
            var message = LocalCredentialService.IsLockedOut(credential) ? LockedMessage(credential) : GenericSignInError;
            return Html(LocalLoginPages.Login(product, returnUrl, csrf, message), StatusCodes.Status401Unauthorized);
        }

        var step = new LoginStep(credential.Id, TotpDone: false);
        if (credential.TotpEnabledAt is not null)
        {
            return Html(LocalLoginPages.Totp(product, returnUrl, csrf, step.Protect(dataProtection), error: null));
        }

        if (credential.MustChangePassword)
        {
            return Html(LocalLoginPages.ChangePassword(product, returnUrl, csrf, step.Protect(dataProtection), error: null));
        }

        return await CompleteSignInAsync(context, db, credentials, credential, usedTotp: false, returnUrl, cancellationToken);
    }

    private static async Task<IResult> SubmitTotpAsync(
        HttpContext context, PlatformDbContext db, LocalCredentialService credentials,
        IDataProtectionProvider dataProtection, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var product = ProductName(configuration);
        var returnUrl = SafeReturnUrl(form["returnUrl"]);

        if (!CsrfValid(context, form["csrf"])
            || LoginStep.Unprotect(dataProtection, form["step"]) is not { TotpDone: false } step)
        {
            return RestartLogin(context, product, returnUrl);
        }

        var csrf = (string)form["csrf"]!;
        var credential = await db.LocalCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == step.CredentialId, cancellationToken);
        if (credential is null || credential.TotpEnabledAt is null || LocalCredentialService.IsLockedOut(credential))
        {
            return RestartLogin(context, product, returnUrl);
        }

        if (!credentials.VerifyTotp(credential, (string?)form["code"] ?? ""))
        {
            await credentials.RegisterFailureAsync(credential, "wrong totp code", ClientIp(context), cancellationToken);
            if (LocalCredentialService.IsLockedOut(credential))
            {
                return RestartLogin(context, product, returnUrl);
            }

            return Html(
                LocalLoginPages.Totp(product, returnUrl, csrf, step.Protect(dataProtection), "That code is not right."),
                StatusCodes.Status401Unauthorized);
        }

        var done = step with { TotpDone = true };
        if (credential.MustChangePassword)
        {
            return Html(LocalLoginPages.ChangePassword(product, returnUrl, csrf, done.Protect(dataProtection), error: null));
        }

        return await CompleteSignInAsync(context, db, credentials, credential, usedTotp: true, returnUrl, cancellationToken);
    }

    private static async Task<IResult> SubmitPasswordChangeAsync(
        HttpContext context, PlatformDbContext db, LocalCredentialService credentials,
        IDataProtectionProvider dataProtection, IConfiguration configuration, CancellationToken cancellationToken)
    {
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var product = ProductName(configuration);
        var returnUrl = SafeReturnUrl(form["returnUrl"]);

        if (!CsrfValid(context, form["csrf"]) || LoginStep.Unprotect(dataProtection, form["step"]) is not { } step)
        {
            return RestartLogin(context, product, returnUrl);
        }

        var csrf = (string)form["csrf"]!;
        var credential = await db.LocalCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == step.CredentialId, cancellationToken);

        // The change page is only reachable AFTER password (and TOTP, when enrolled) verified — the
        // step token proves it. A TOTP-enrolled credential whose token says otherwise restarts.
        if (credential is null || LocalCredentialService.IsLockedOut(credential)
            || (credential.TotpEnabledAt is not null && !step.TotpDone))
        {
            return RestartLogin(context, product, returnUrl);
        }

        var password = (string?)form["password"] ?? "";
        if (!string.Equals(password, form["confirm"], StringComparison.Ordinal))
        {
            return Html(
                LocalLoginPages.ChangePassword(product, returnUrl, csrf, step.Protect(dataProtection),
                    "The two passwords do not match."),
                StatusCodes.Status400BadRequest);
        }

        if (await credentials.SetPasswordAsync(
                credential, password, mustChange: false, byAdminReset: false,
                ClientIp(context), cancellationToken) is { } error)
        {
            return Html(
                LocalLoginPages.ChangePassword(product, returnUrl, csrf, step.Protect(dataProtection), error),
                StatusCodes.Status400BadRequest);
        }

        return await CompleteSignInAsync(
            context, db, credentials, credential, usedTotp: step.TotpDone, returnUrl, cancellationToken);
    }

    private static async Task<IResult> CompleteSignInAsync(
        HttpContext context, PlatformDbContext db, LocalCredentialService credentials,
        LocalCredential credential, bool usedTotp, string returnUrl, CancellationToken cancellationToken)
    {
        var user = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == credential.UserId, cancellationToken);
        var tenantActive = user is not null
            && await db.Tenants.AnyAsync(t => t.Id == user.TenantId && t.IsActive, cancellationToken);
        if (user is not { IsActive: true } || !tenantActive)
        {
            // True but unspecific on purpose; the audit trail carries the specifics.
            return Html(LocalLoginPages.Login(
                    ProductName(context.RequestServices.GetRequiredService<IConfiguration>()),
                    returnUrl, IssueCsrf(context), "This account is not active."),
                StatusCodes.Status403Forbidden);
        }

        await credentials.RegisterSignInAsync(credential, user, usedTotp, ClientIp(context), cancellationToken);

        var identity = new ClaimsIdentity(LocalAuthDefaults.CookieScheme);
        identity.AddClaim(new Claim(LocalAuthDefaults.UserIdClaim, user.Id.ToString()));
        identity.AddClaim(new Claim(LocalAuthDefaults.StampClaim, credential.SecurityStamp));
        identity.AddClaim(new Claim(Claims.Name, user.DisplayName ?? user.Email));
        identity.AddClaim(new Claim(Claims.AuthenticationMethodReference, "pwd"));
        if (usedTotp)
        {
            identity.AddClaim(new Claim(Claims.AuthenticationMethodReference, "otp"));
        }

        await context.SignInAsync(LocalAuthDefaults.CookieScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(returnUrl);
    }

    /// <summary>An invalid/expired step or a mid-flow lockout goes back to square one, quietly.</summary>
    private static IResult RestartLogin(HttpContext context, string product, string returnUrl) =>
        Html(LocalLoginPages.Login(product, returnUrl, IssueCsrf(context),
            "Please sign in again."), StatusCodes.Status401Unauthorized);

    // ── Small pieces ─────────────────────────────────────────────────────────

    /// <summary>
    /// The multi-step login's state: which credential proved what, signed and time-boxed with Data
    /// Protection so the server holds no session until the final cookie.
    /// </summary>
    private sealed record LoginStep(Guid CredentialId, bool TotpDone)
    {
        public string Protect(IDataProtectionProvider dataProtection)
        {
            var expires = DateTimeOffset.UtcNow.Add(StepLifetime).ToUnixTimeSeconds();
            var payload = $"{CredentialId:N}|{(TotpDone ? 1 : 0)}|{expires}";
            return WebEncoders.Base64UrlEncode(
                dataProtection.CreateProtector(StepProtectorPurpose).Protect(Encoding.UTF8.GetBytes(payload)));
        }

        public static LoginStep? Unprotect(IDataProtectionProvider dataProtection, string? token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return null;
            }

            try
            {
                var payload = Encoding.UTF8.GetString(
                    dataProtection.CreateProtector(StepProtectorPurpose)
                        .Unprotect(WebEncoders.Base64UrlDecode(token)));
                var parts = payload.Split('|');
                if (parts.Length != 3
                    || !Guid.TryParseExact(parts[0], "N", out var credentialId)
                    || !long.TryParse(parts[2], out var expires)
                    || DateTimeOffset.FromUnixTimeSeconds(expires) < DateTimeOffset.UtcNow)
                {
                    return null;
                }

                return new LoginStep(credentialId, parts[1] == "1");
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null; // tampered, truncated, or from a rotated key ring — all just "expired"
            }
        }
    }

    /// <summary>
    /// A real hash of an unknowable password, verified against for nonexistent accounts so both
    /// failure kinds cost the same wall-clock.
    /// </summary>
    private static class DummyCredential
    {
        public static readonly LocalCredential Value = new()
        {
            Email = "timing@localhost",
            PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>()
                .HashPassword(null!, Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))),
            SecurityStamp = "dummy",
        };
    }

    private static string IssueCsrf(HttpContext context)
    {
        var value = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        context.Response.Cookies.Append(CsrfCookieName, value, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
            Path = "/auth",
            MaxAge = TimeSpan.FromHours(2),
        });
        return value;
    }

    /// <summary>Double-submit check: the hidden field must equal the cookie only our origin can set.</summary>
    private static bool CsrfValid(HttpContext context, string? field) =>
        context.Request.Cookies.TryGetValue(CsrfCookieName, out var cookie)
        && !string.IsNullOrEmpty(cookie)
        && !string.IsNullOrEmpty(field)
        && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(field));

    /// <summary>Only a local path may follow a sign-in — never an absolute or protocol-relative URL.</summary>
    private static string SafeReturnUrl(string? value) =>
        !string.IsNullOrEmpty(value) && value[0] == '/' && !value.StartsWith("//", StringComparison.Ordinal)
        && !value.Contains('\\', StringComparison.Ordinal)
            ? value
            : "/";

    private static string LockedMessage(LocalCredential credential)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(
            ((credential.LockedUntil ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow).TotalMinutes));
        return $"Too many attempts. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.";
    }

    private static string ProductName(IConfiguration configuration) =>
        configuration["Branding:ProductName"] ?? "Plenipo";

    private static string? ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    private static IResult Html(string content, int statusCode = StatusCodes.Status200OK) =>
        Results.Content(content, "text/html; charset=utf-8", contentEncoding: null, statusCode);
}

using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.AspNetCore.RateLimiting;

/// <summary>
/// Per-user rate limiting for the LLM-backed chat endpoints — a cost/abuse backstop for an AI platform.
/// Partitions by the caller's identity (the <c>sub</c> claim), so one user spamming turns can't exhaust the
/// model budget or starve others; each principal gets its own fixed window. Applied only to the chat
/// surfaces via <see cref="ChatPolicy"/>; the rest of the API is unaffected.
/// </summary>
public static class RateLimitingSetup
{
    /// <summary>Named policy applied to the chat / AG-UI / stream endpoints.</summary>
    public const string ChatPolicy = "plenipo-chat";

    /// <summary>Named policy for anonymous PUBLIC endpoints (checkout) — partitioned by client IP.</summary>
    public const string PublicPolicy = "plenipo-public";

    /// <summary>
    /// Named policy for the local-auth surface (login POSTs, token endpoint) — partitioned by client
    /// IP. Wide enough for a NAT'd office of users refreshing tokens, tight enough that credential
    /// stuffing runs into it immediately; per-credential lockout is enforced besides.
    /// </summary>
    public const string AuthPolicy = "plenipo-auth";

    public static IServiceCollection AddPlenipoRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
        var permitsPerMinute = options.ChatPermitsPerMinute > 0
            ? options.ChatPermitsPerMinute
            : RateLimitOptions.DefaultChatPermitsPerMinute;

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };

            limiter.AddPolicy(ChatPolicy, httpContext =>
            {
                var key = httpContext.User.FindFirstValue("sub")
                    ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? "anonymous";

                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                });
            });

            // Anonymous public surfaces can't partition by principal — partition by IP, tightly.
            limiter.AddPolicy(PublicPolicy, httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                });
            });

            limiter.AddPolicy(AuthPolicy, httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                });
            });
        });

        return services;
    }
}

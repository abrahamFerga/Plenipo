using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Plenipo.AspNetCore.Hosting;

/// <summary>
/// Serves the Plenipo <b>admin console</b> — the <c>@plenipo/admin-ui</c> SPA — directly from the API host
/// at <c>/admin</c>. This is the platform's analogue of OpenClaw's "control UI built into the gateway":
/// every Plenipo host gets a security / RBAC / users / token-usage / audit console for free, served from
/// the same origin as the API and independent of whatever <i>domain</i> UI the product ships.
///
/// The console is just static assets; the data it reads lives under <c>/api/admin/*</c>, which stays
/// RBAC-gated server-side (see <c>AdminEndpoints</c>). Serving the shell unauthenticated mirrors how the
/// domain SPA is served — the API, not the asset host, is the security boundary.
/// </summary>
public static class AdminConsoleExtensions
{
    /// <summary>The request path the console is mounted under by convention.</summary>
    public const string DefaultRequestPath = "/admin";

    /// <summary>The content-root-relative directory a host drops the built console assets into.</summary>
    public const string DefaultAssetDirectory = "wwwroot/admin";

    /// <summary>
    /// Mounts the admin console SPA at <paramref name="requestPath"/> (default <c>/admin</c>), serving its
    /// static assets and falling back to <c>index.html</c> for client-side deep links (e.g. <c>/admin/users</c>).
    /// If the asset directory is absent — the console hasn't been built into this host — the call logs a
    /// notice and is a no-op, so the API still runs. Call after <c>UseAuthorization()</c>.
    /// </summary>
    /// <param name="app">The web application.</param>
    /// <param name="assetPath">
    /// Absolute or content-root-relative path to the built console (the admin-ui <c>dist/</c> output).
    /// Defaults to <see cref="DefaultAssetDirectory"/> under the content root.
    /// </param>
    /// <param name="requestPath">The path prefix to mount under. Defaults to <see cref="DefaultRequestPath"/>.</param>
    public static WebApplication UsePlenipoAdminConsole(
        this WebApplication app,
        string? assetPath = null,
        string requestPath = DefaultRequestPath)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Plenipo.AdminConsole");

        var configured = assetPath ?? DefaultAssetDirectory;
        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(app.Environment.ContentRootPath, configured);

        if (!Directory.Exists(root))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Plenipo admin console assets not found at {Path}; the console will not be served. " +
                    "Build @plenipo/admin-ui and copy its dist/ output there to enable it.",
                    root);
            }
            return app;
        }

        var provider = new PhysicalFileProvider(Path.GetFullPath(root));

        // 1. Serve real files (JS/CSS/assets) under the request path. A matching file short-circuits here.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            RequestPath = requestPath,
        });

        // 2. SPA fallback: any GET under the request path that didn't resolve to a file returns index.html,
        //    so the client-side router can handle deep links. Scoped to the prefix, so it never shadows the
        //    /api/admin/* data endpoints.
        app.MapWhen(
            ctx => HttpMethods.IsGet(ctx.Request.Method)
                   && ctx.Request.Path.StartsWithSegments(requestPath),
            branch => branch.Run(async ctx =>
            {
                var index = provider.GetFileInfo("index.html");
                if (!index.Exists)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.SendFileAsync(index);
            }));

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Plenipo admin console mounted at {RequestPath} from {Path}", requestPath, root);
        }
        return app;
    }
}

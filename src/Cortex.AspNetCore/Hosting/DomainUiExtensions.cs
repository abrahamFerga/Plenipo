using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Cortex.AspNetCore.Hosting;

/// <summary>
/// Serves the product's <b>domain UI</b> — the <c>@abrahamferga/cortex-ui</c> SPA, built with the product's own
/// branding — directly from the API host at <c>/</c>, exactly like <see cref="AdminConsoleExtensions"/>
/// serves the admin console at <c>/admin</c>. This is the no-registry distribution path: a product
/// builds the SPA once (baking in its brand name and a same-origin API base) and drops the
/// <c>dist/</c> output into <c>wwwroot/app</c>; the host then IS the web app, same origin as the API,
/// no npm registry, no CORS, no separate asset host.
///
/// The shell is static; every byte of data it reads comes from the RBAC-gated <c>/api/*</c> surface.
/// Reserved prefixes (<c>/api</c>, <c>/admin</c>, <c>/hubs</c>, <c>/webhooks</c>, health, OpenAPI)
/// are never shadowed, so all platform endpoints keep working unchanged.
/// </summary>
public static class DomainUiExtensions
{
    /// <summary>The content-root-relative directory a host drops the built SPA into.</summary>
    public const string DefaultAssetDirectory = "wwwroot/app";

    private static readonly string[] ReservedPrefixes =
        ["/api", "/admin", "/hubs", "/webhooks", "/health", "/alive", "/openapi", "/scalar"];

    /// <summary>
    /// Mounts the domain UI at the site root, serving its static assets and falling back to
    /// <c>index.html</c> for client-side deep links (e.g. <c>/legal/matters</c>). If the asset
    /// directory is absent — the host runs the UI from a dev server instead, or is API-only —
    /// the call logs a notice and is a no-op. Call after <c>UseAuthorization()</c>.
    /// </summary>
    public static WebApplication UseCortexDomainUi(this WebApplication app, string? assetPath = null)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cortex.DomainUi");

        var configured = assetPath ?? DefaultAssetDirectory;
        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(app.Environment.ContentRootPath, configured);

        if (!Directory.Exists(root))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Cortex domain UI assets not found at {Path}; the SPA will not be served from this host. " +
                    "Build @abrahamferga/cortex-ui with your product's branding and copy its dist/ output there to enable it.",
                    root);
            }
            return app;
        }

        var provider = new PhysicalFileProvider(Path.GetFullPath(root));

        // 1. Serve real files (JS/CSS/assets) at the root. A matching file short-circuits here.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
        });

        // 2. SPA fallback: any GET outside the reserved prefixes that didn't resolve to a file returns
        //    index.html, so the client-side router handles deep links. Reserved prefixes pass through
        //    untouched to the platform endpoints.
        app.MapWhen(
            ctx => HttpMethods.IsGet(ctx.Request.Method)
                   && !ReservedPrefixes.Any(p => ctx.Request.Path.StartsWithSegments(p)),
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
            logger.LogInformation("Cortex domain UI mounted at / from {Path}", root);
        }
        return app;
    }
}

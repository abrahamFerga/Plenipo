using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace Plenipo.AspNetCore.Hosting;

/// <summary>
/// Content-Security-Policy support for the shells the host serves. Both SPAs carry one inline
/// script (the theme initializer that stops a dark-mode reload flashing light), and a product that
/// ships a strict CSP used to pin that script's SHA-256 — so any platform change to the shell's
/// HTML white-screened the product, and no compile-and-test gate could see it. The host now stamps
/// a <b>per-request nonce</b> onto every <c>&lt;script&gt;</c> in a served shell; a product's own CSP
/// middleware asks <see cref="NonceFor"/> for the same value and emits <c>'nonce-…'</c> instead of a
/// hash. The platform sets no policy header itself — what a product allows is the product's call —
/// it only makes a nonce-based policy possible.
/// </summary>
/// <example>
/// <code>
/// app.Use(async (context, next) =>
/// {
///     var nonce = PlenipoCsp.NonceFor(context);
///     context.Response.Headers["Content-Security-Policy"] =
///         $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'";
///     await next();
/// });
/// </code>
/// </example>
public static partial class PlenipoCsp
{
    /// <summary>The <see cref="HttpContext.Items"/> key the request's nonce is kept under.</summary>
    public const string NonceItemKey = "Plenipo.CspNonce";

    /// <summary>
    /// The nonce for this request — created on first call, then the same value for the rest of the
    /// request, so a product middleware that runs before the shell is served and the shell itself
    /// agree. 128 bits from the platform's CSPRNG, base64.
    /// </summary>
    public static string NonceFor(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(NonceItemKey, out var existing) && existing is string nonce)
        {
            return nonce;
        }

        nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[NonceItemKey] = nonce;
        return nonce;
    }

    /// <summary>
    /// Adds <c>nonce="…"</c> to every <c>&lt;script&gt;</c> tag that does not already carry one —
    /// inline and external alike, so a policy of <c>script-src 'nonce-…'</c> alone admits the whole
    /// shell. Pure, so it is unit-testable and cheap enough to run per request.
    /// </summary>
    public static string StampNonces(string html, string nonce)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        return ScriptTagWithoutNonce().Replace(html, $"<script nonce=\"{nonce}\"");
    }

    /// <summary>
    /// Writes a shell's <c>index.html</c> with this request's nonce stamped in. <c>Cache-Control:
    /// no-store</c>, because a response carrying a nonce is valid for exactly one request.
    /// </summary>
    internal static async Task ServeShellAsync(HttpContext context, IFileInfo index)
    {
        var nonce = NonceFor(context);

        string html;
        await using (var stream = index.CreateReadStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            html = await reader.ReadToEndAsync(context.RequestAborted);
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(StampNonces(html, nonce), context.RequestAborted);
    }

    // "<script" that is a tag (followed by whitespace or ">"), whose attributes up to ">" contain no nonce yet.
    [GeneratedRegex(@"<script(?![^>]*\bnonce=)(?=[\s>])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptTagWithoutNonce();
}

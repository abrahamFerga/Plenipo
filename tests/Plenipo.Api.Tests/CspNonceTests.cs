using Plenipo.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The pure half of nonce-based CSP for the served shells: every script tag gets the request's nonce,
/// a tag that already has one is left alone, and the nonce is stable within a request and fresh
/// across requests. The serving half (headers, both shells, the product's policy admitting the
/// nonce) is proven end to end in the samples suite's DomainUiServingTests.
/// </summary>
public sealed class CspNonceTests
{
    [Fact]
    public void Stamps_inline_and_external_scripts_and_leaves_nonced_ones_alone()
    {
        const string html =
            "<html><head><script>init()</script>\n" +
            "<SCRIPT type=\"module\" src=\"/app.js\"></SCRIPT>\n" +
            "<script nonce=\"already\">x()</script></head></html>";

        var stamped = PlenipoCsp.StampNonces(html, "abc123");

        Assert.Contains("<script nonce=\"abc123\">init()</script>", stamped, StringComparison.Ordinal);
        Assert.Contains("<script nonce=\"abc123\" type=\"module\" src=\"/app.js\">", stamped, StringComparison.Ordinal);
        Assert.Contains("<script nonce=\"already\">x()</script>", stamped, StringComparison.Ordinal);
        Assert.Equal(3, stamped.Split("nonce=").Length - 1);
    }

    [Fact]
    public void Does_not_touch_text_that_merely_starts_with_script()
    {
        // "<scripts>" is not a script tag; a description mentioning scripts is not either.
        const string html = "<scripts>not a tag</scripts><p>the script runs</p>";

        Assert.Equal(html, PlenipoCsp.StampNonces(html, "n"));
    }

    [Fact]
    public void Nonce_is_stable_within_a_request_and_fresh_across_requests()
    {
        var first = new DefaultHttpContext();
        var second = new DefaultHttpContext();

        var a = PlenipoCsp.NonceFor(first);
        var again = PlenipoCsp.NonceFor(first);
        var b = PlenipoCsp.NonceFor(second);

        Assert.Equal(a, again);
        Assert.NotEqual(a, b);
        Assert.Equal(16, Convert.FromBase64String(a).Length);
        Assert.Equal(a, first.Items[PlenipoCsp.NonceItemKey]);
    }
}

using System.Text.Encodings.Web;

namespace Plenipo.AspNetCore.Auth.Local;

/// <summary>
/// The issuer's server-rendered pages (ADR 0003): sign-in, the TOTP step, and the forced password
/// change. String-built on purpose — no Razor, no client framework — because the whole surface is
/// three small forms whose only dynamic content is encoded text, and the host must render them
/// before any SPA bundle is involved. Styling is one inline sheet honoring the OS color scheme.
/// </summary>
internal static class LocalLoginPages
{
    private static readonly HtmlEncoder Html = HtmlEncoder.Default;

    internal static string Login(string product, string returnUrl, string csrf, string? error) =>
        Page(product, "Sign in", $"""
            <h1>{Html.Encode(product)}</h1>
            <p class="sub">Sign in to continue</p>
            {ErrorHtml(error)}
            <form method="post" action="{LocalAuthDefaults.LoginPath}">
              {Hidden(returnUrl, csrf, step: null)}
              <label for="email">Email</label>
              <input id="email" name="email" type="email" autocomplete="username" required autofocus />
              <label for="password">Password</label>
              <input id="password" name="password" type="password" autocomplete="current-password" required />
              <button type="submit">Sign in</button>
            </form>
            """);

    internal static string Totp(string product, string returnUrl, string csrf, string step, string? error) =>
        Page(product, "Verification code", $"""
            <h1>{Html.Encode(product)}</h1>
            <p class="sub">Enter the 6-digit code from your authenticator app</p>
            {ErrorHtml(error)}
            <form method="post" action="{LocalAuthDefaults.LoginPath}/totp">
              {Hidden(returnUrl, csrf, step)}
              <label for="code">Verification code</label>
              <input id="code" name="code" inputmode="numeric" pattern="[0-9 ]*" autocomplete="one-time-code"
                     required autofocus />
              <button type="submit">Verify</button>
            </form>
            """);

    internal static string ChangePassword(string product, string returnUrl, string csrf, string step, string? error) =>
        Page(product, "Choose a new password", $"""
            <h1>{Html.Encode(product)}</h1>
            <p class="sub">Your password must be changed before you continue. Use at least 12 characters.</p>
            {ErrorHtml(error)}
            <form method="post" action="{LocalAuthDefaults.LoginPath}/change">
              {Hidden(returnUrl, csrf, step)}
              <label for="password">New password</label>
              <input id="password" name="password" type="password" autocomplete="new-password" required autofocus />
              <label for="confirm">Repeat new password</label>
              <input id="confirm" name="confirm" type="password" autocomplete="new-password" required />
              <button type="submit">Change password and sign in</button>
            </form>
            """);

    private static string Hidden(string returnUrl, string csrf, string? step) =>
        $"""
        <input type="hidden" name="returnUrl" value="{Html.Encode(returnUrl)}" />
        <input type="hidden" name="csrf" value="{Html.Encode(csrf)}" />
        """ + (step is null ? "" : $"""<input type="hidden" name="step" value="{Html.Encode(step)}" />""");

    private static string ErrorHtml(string? error) =>
        error is null ? "" : $"""<p class="error" role="alert">{Html.Encode(error)}</p>""";

    private static string Page(string product, string title, string body) =>
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <meta name="color-scheme" content="light dark" />
        <title>{{Html.Encode(title)}} · {{Html.Encode(product)}}</title>
        <style>
          :root { color-scheme: light dark; }
          body { margin: 0; min-height: 100dvh; display: grid; place-items: center;
                 font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif;
                 background: Canvas; color: CanvasText; }
          main { width: min(92vw, 22rem); padding: 2rem;
                 border: 1px solid color-mix(in srgb, CanvasText 15%, transparent);
                 border-radius: 12px; }
          h1 { font-size: 1.25rem; margin: 0; }
          .sub { margin: .25rem 0 1.25rem; opacity: .7; font-size: .9rem; }
          label { display: block; font-size: .8rem; font-weight: 600; margin: .9rem 0 .25rem; }
          input { width: 100%; box-sizing: border-box; padding: .55rem .65rem; font: inherit;
                  border: 1px solid color-mix(in srgb, CanvasText 25%, transparent);
                  border-radius: 8px; background: transparent; color: inherit; }
          button { width: 100%; margin-top: 1.25rem; padding: .6rem; font: inherit; font-weight: 600;
                   border: 0; border-radius: 8px; cursor: pointer;
                   background: color-mix(in srgb, CanvasText 88%, Canvas); color: Canvas; }
          .error { margin: 0 0 .5rem; padding: .5rem .65rem; border-radius: 8px; font-size: .85rem;
                   background: color-mix(in srgb, #d33 12%, Canvas);
                   border: 1px solid color-mix(in srgb, #d33 45%, transparent); }
        </style>
        </head>
        <body><main>{{body}}</main></body>
        </html>
        """;
}

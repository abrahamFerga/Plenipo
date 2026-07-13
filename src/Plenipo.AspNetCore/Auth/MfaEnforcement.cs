using System.Security.Claims;
using System.Text.Json;

namespace Plenipo.AspNetCore.Auth;

/// <summary>
/// Judges whether a validated token was issued after multi-factor authentication, from its
/// <c>amr</c> (authentication method references) claim. Pure by design so the policy is
/// unit-testable without standing up a JWT pipeline; <c>AddPlenipoAuthentication</c> wires it into
/// <c>OnTokenValidated</c> when <see cref="AuthOptions.RequireMfa"/> is set.
/// </summary>
public static class MfaEnforcement
{
    /// <summary>
    /// True when any <c>amr</c> value matches <see cref="AuthOptions.MfaAmrValues"/>. Handles both
    /// wire shapes: one claim per method (how JsonWebToken surfaces JWT arrays) and a single claim
    /// holding the raw JSON array (how some IdPs and older handlers pass it through).
    /// </summary>
    public static bool SatisfiesMfa(ClaimsPrincipal principal, AuthOptions options)
    {
        var accepted = options.MfaAmrValues;
        foreach (var claim in principal.FindAll("amr"))
        {
            var value = claim.Value;
            if (value.StartsWith('['))
            {
                string[]? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<string[]>(value);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (parsed is not null && parsed.Any(v => accepted.Contains(v, StringComparer.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            else if (accepted.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Plenipo.Infrastructure.Agents.Security;

/// <summary>
/// Fast, deterministic first-pass protection for common PII and credentials. This intentionally does not
/// claim semantic PII coverage; deployments needing names, addresses, health identifiers, or locale-specific
/// entities should add a specialized DLP/PII provider behind <c>IAgentSecurityService</c>.
/// </summary>
internal static partial class SensitiveDataDetector
{
    internal sealed record Detection(
        string RedactedText,
        IReadOnlyList<string> Categories,
        bool Unavailable = false)
    {
        public bool HasMatches => Categories.Count > 0;
    }

    private sealed record MatchSpan(int Index, int Length, string Category);

    public static Detection DetectAndRedact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new(text, []);
        }

        var matches = new List<MatchSpan>();
        try
        {
            Add(matches, EmailRegex().Matches(text), "Email");
            Add(matches, SocialSecurityNumberRegex().Matches(text), "UsSsn");
            Add(matches, PhoneRegex().Matches(text), "PhoneNumber");
            Add(matches, CredentialRegex().Matches(text), "Credential");
            Add(matches, JwtRegex().Matches(text), "Jwt");

            foreach (Match match in CreditCardCandidateRegex().Matches(text))
            {
                var digits = new string(match.Value.Where(char.IsAsciiDigit).ToArray());
                if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits))
                {
                    matches.Add(new MatchSpan(match.Index, match.Length, "CreditCard"));
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A caller enforcing security must treat this as unavailable/fail-closed, while audit
            // serialization uses a fixed placeholder so it can never fall back to logging raw values.
            return new(text, [], Unavailable: true);
        }

        if (matches.Count == 0)
        {
            return new(text, []);
        }

        // Prefer the longest match at a given offset, then remove overlaps so a credential containing another
        // recognizable shape is replaced once. Work backwards to keep recorded offsets valid.
        var selected = new List<MatchSpan>();
        var end = -1;
        foreach (var match in matches.OrderBy(m => m.Index).ThenByDescending(m => m.Length))
        {
            if (match.Index < end)
            {
                continue;
            }

            selected.Add(match);
            end = match.Index + match.Length;
        }

        var redacted = new StringBuilder(text);
        foreach (var match in selected.OrderByDescending(m => m.Index))
        {
            redacted.Remove(match.Index, match.Length);
            redacted.Insert(match.Index, $"[REDACTED:{match.Category.ToUpperInvariant()}]");
        }

        return new(redacted.ToString(), selected.Select(m => m.Category).Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Redacts string-valued tool arguments in place before execution or persistence.</summary>
    public static void RedactArguments(AIFunctionArguments? arguments)
    {
        if (arguments is null)
        {
            return;
        }

        foreach (var key in arguments.Keys.ToArray())
        {
            arguments[key] = RedactValue(arguments[key]);
        }
    }

    public static string RedactSerialized(string? serialized)
    {
        if (string.IsNullOrEmpty(serialized))
        {
            return serialized ?? string.Empty;
        }

        var detection = DetectAndRedact(serialized);
        return detection.Unavailable ? "[REDACTION_UNAVAILABLE]" : detection.RedactedText;
    }

    private static object? RedactValue(object? value) => value switch
    {
        string text => DetectAndRedact(text).RedactedText,
        JsonElement element => RedactJsonElement(element),
        IReadOnlyDictionary<string, object?> dictionary =>
            dictionary.ToDictionary(kv => kv.Key, kv => RedactValue(kv.Value), StringComparer.Ordinal),
        IDictionary<string, object?> dictionary =>
            dictionary.ToDictionary(kv => kv.Key, kv => RedactValue(kv.Value), StringComparer.Ordinal),
        IEnumerable<object?> sequence => sequence.Select(RedactValue).ToArray(),
        _ => value,
    };

    private static object? RedactJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => DetectAndRedact(element.GetString() ?? string.Empty).RedactedText,
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => RedactJsonElement(property.Value),
                StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(RedactJsonElement).ToArray(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.Clone(),
    };

    private static void Add(List<MatchSpan> destination, MatchCollection matches, string category)
    {
        foreach (Match match in matches)
        {
            destination.Add(new MatchSpan(match.Index, match.Length, category));
        }
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleDigit = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var digit = digits[i] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubleDigit = !doubleDigit;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(
        @"(?<![\w.+-])[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}(?![\w.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(
        @"(?<!\d)(?!000|666|9\d\d)\d{3}[- ](?!00)\d{2}[- ](?!0000)\d{4}(?!\d)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex SocialSecurityNumberRegex();

    [GeneratedRegex(
        @"(?<![\w\d])(?:\+?[1-9]\d{0,2}[\s.-]?)?(?:\(?\d{3}\)?[\s.-]?)\d{3}[\s.-]\d{4}(?![\w\d])",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(
        @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex CreditCardCandidateRegex();

    [GeneratedRegex(
        @"\b(?:sk-(?:proj-)?[A-Z0-9_-]{16,}|gh[pousr]_[A-Z0-9]{20,}|AIza[A-Z0-9_-]{20,})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(
        @"\beyJ[A-Z0-9_-]{5,}\.eyJ[A-Z0-9_-]{5,}\.[A-Z0-9_-]{10,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex JwtRegex();
}

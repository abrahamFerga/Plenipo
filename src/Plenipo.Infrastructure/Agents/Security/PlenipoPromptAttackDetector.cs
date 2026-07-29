using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Plenipo.Infrastructure.Agents.Security;

/// <summary>
/// Dependency-free first-pass prompt-attack detector owned by Plenipo. It deliberately focuses on
/// explicit instruction override, authority bypass, prompt extraction, and exfiltration signals. It
/// complements—rather than claims parity with—a trained classifier such as Prompt Guard or Prompt Shields.
/// </summary>
internal static partial class PlenipoPromptAttackDetector
{
    internal sealed record Detection(bool AttackDetected, IReadOnlyList<string> Categories);

    private static readonly string[] CompactAttackPhrases =
    [
        "ignorepreviousinstructions",
        "ignoreallpreviousinstructions",
        "disregardpreviousinstructions",
        "overridesysteminstructions",
        "revealsystemprompt",
        "printsystemprompt",
        "systemoverride",
        "bypasssecuritychecks",
        "bypassapproval",
    ];

    public static Detection Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new(false, []);
        }

        var normalized = Normalize(text, out var removedHiddenCharacters);
        var (score, categories) = ScanNormalized(normalized);
        if (removedHiddenCharacters && score > 0)
        {
            categories.Add("ObfuscatedControlCharacters");
            score++;
        }

        // Encoded payloads are common in attack corpora. Decode only bounded, valid UTF-8 candidates
        // and require the decoded text itself to contain strong attack signals.
        var encodedSource = WebUtility.HtmlDecode(text.Normalize(NormalizationForm.FormKC));
        foreach (Match match in Base64CandidateRegex().Matches(encodedSource).Cast<Match>().Take(8))
        {
            if (match.Length > 4096 || match.Length % 4 != 0)
            {
                continue;
            }

            try
            {
                var bytes = Convert.FromBase64String(match.Value);
                var decoded = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(bytes);
                var (decodedScore, decodedCategories) = ScanNormalized(Normalize(decoded, out _));
                if (decodedScore >= 2)
                {
                    score += decodedScore;
                    categories.Add("EncodedPromptAttack");
                    categories.AddRange(decodedCategories);
                }
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            {
                // Not a valid encoded instruction; it is ordinary content.
            }
        }

        return new(
            score >= 2,
            categories.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static (int Score, List<string> Categories) ScanNormalized(string normalized)
    {
        var score = 0;
        var categories = new List<string>();

        AddSignal(InstructionOverrideRegex().IsMatch(normalized), 3, "InstructionOverride");
        AddSignal(PromptExtractionRegex().IsMatch(normalized), 2, "PromptExtraction");
        AddSignal(DataExfiltrationRegex().IsMatch(normalized), 3, "DataExfiltration");
        AddSignal(AuthorityBypassRegex().IsMatch(normalized), 2, "AuthorityBypass");
        AddSignal(RoleDelimiterRegex().IsMatch(normalized), 1, "RoleImpersonation");
        AddSignal(ModelControlRegex().IsMatch(normalized), 3, "ModelControl");

        var compact = new string(normalized.Where(char.IsLetterOrDigit).ToArray());
        AddSignal(
            CompactAttackPhrases.Any(compact.Contains),
            3,
            "ObfuscatedInstructionOverride");

        return (score, categories);

        void AddSignal(bool detected, int weight, string category)
        {
            if (!detected)
            {
                return;
            }

            score += weight;
            categories.Add(category);
        }
    }

    private static string Normalize(string text, out bool removedHiddenCharacters)
    {
        var decoded = WebUtility.HtmlDecode(text.Normalize(NormalizationForm.FormKC));
        var builder = new StringBuilder(decoded.Length);
        var previousWasSpace = false;
        removedHiddenCharacters = false;

        foreach (var rune in decoded.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Format or UnicodeCategory.Control &&
                rune.Value is not ('\r' or '\n' or '\t'))
            {
                removedHiddenCharacters = true;
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }

                continue;
            }

            foreach (var value in rune.ToString())
            {
                builder.Append(char.ToLowerInvariant(value));
            }

            previousWasSpace = false;
        }

        return builder.ToString();
    }

    [GeneratedRegex(
        @"\b(?:ignore|disregard|forget|override|bypass|supersede)\b[\s\S]{0,80}\b(?:previous|prior|above|system|developer|security|safety)\b[\s\S]{0,50}\b(?:instructions?|prompts?|rules?|polic(?:y|ies))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex InstructionOverrideRegex();

    [GeneratedRegex(
        @"\b(?:reveal|show|print|repeat|leak|expose|return)\b[\s\S]{0,60}\b(?:system|developer|hidden|initial|internal)\b[\s\S]{0,30}\b(?:prompts?|instructions?|messages?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PromptExtractionRegex();

    [GeneratedRegex(
        @"\b(?:send|post|upload|forward|exfiltrate|transmit)\b[\s\S]{0,80}\b(?:secrets?|credentials?|tokens?|api[\s_-]*keys?|\.env|confidential|private[\s_-]*data)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DataExfiltrationRegex();

    [GeneratedRegex(
        @"\b(?:without|bypass|skip|avoid|disable)\b[\s\S]{0,50}\b(?:approval|authorization|permission|audit|guardrails?|security checks?|safety checks?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AuthorityBypassRegex();

    [GeneratedRegex(
        @"(?:<\|/?(?:system|developer|assistant)\|?>|\[(?:system|developer)(?:\s+message)?\]|(?:^|\n)\s*(?:system|developer)\s*(?:message)?\s*:)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RoleDelimiterRegex();

    [GeneratedRegex(
        @"\b(?:you are now|new (?:system|developer) instructions?|system override|developer mode|do anything now|dan mode|simulate unrestricted)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ModelControlRegex();

    [GeneratedRegex(
        @"[A-Za-z0-9+/]{24,}={0,2}",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Base64CandidateRegex();
}

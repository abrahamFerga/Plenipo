using System.Globalization;
using Plenipo.Core.Platform;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// Picks the Postgres text-search configuration to index a document with. Stemming and stop-words
/// are language-specific, so indexing Spanish contracts as English costs real recall — but getting
/// the language wrong costs more than not guessing, so this is deliberately conservative: script
/// first (decisive when it applies), then stop-word voting for Latin script, and
/// <see cref="RagLanguage.Default"/> ("simple": no stemming, no stop-words) whenever the evidence
/// is thin. No dependency, no model download, and small enough to reason about.
/// </summary>
public static class LanguageDetector
{
    /// <summary>A document shorter than this carries too little signal to vote on.</summary>
    private const int MinimumCharacters = 40;

    /// <summary>The winner must out-score the runner-up by this ratio, or we decline to guess.</summary>
    private const double WinMargin = 1.5;

    /// <summary>And must cover at least this share of sampled tokens — otherwise it's noise.</summary>
    private const double MinimumHitRate = 0.04;

    /// <summary>Only the head of a document is sampled; language rarely changes mid-file.</summary>
    private const int SampleCharacters = 4000;

    /// <summary>
    /// The most frequent function words per language — the classic cheap signal. Chosen for
    /// discrimination rather than raw frequency (Spanish "de" also being Portuguese does no work, so
    /// the sets lean on the words that differ).
    /// </summary>
    private static readonly (string Language, string[] Words)[] StopWords =
    [
        ("english", ["the", "and", "of", "to", "that", "shall", "with", "which", "is", "are", "this", "for", "be", "not", "or", "any"]),
        ("spanish", ["que", "los", "las", "del", "por", "para", "con", "una", "como", "pero", "está", "más", "ser", "según", "sobre", "este"]),
        ("portuguese", ["que", "não", "uma", "com", "para", "como", "mais", "dos", "das", "pelo", "pela", "são", "está", "isso", "também", "quando"]),
        ("french", ["les", "des", "une", "que", "pour", "dans", "par", "sur", "avec", "est", "sont", "cette", "aux", "plus", "être", "leur"]),
        ("german", ["der", "die", "und", "den", "das", "von", "mit", "für", "ist", "nicht", "auch", "eine", "auf", "dem", "wird", "durch"]),
        ("italian", ["che", "non", "per", "con", "una", "sono", "come", "alla", "dei", "delle", "nel", "più", "questo", "essere", "anche", "dal"]),
        ("dutch", ["het", "van", "een", "dat", "niet", "zijn", "met", "voor", "aan", "wordt", "door", "als", "ook", "deze", "worden", "maar"]),
        ("danish", ["det", "til", "ikke", "der", "som", "med", "for", "har", "kan", "eller", "efter", "ved", "skal", "være", "denne", "fra"]),
        ("swedish", ["och", "att", "det", "som", "för", "inte", "med", "har", "den", "till", "ett", "eller", "kan", "skall", "vara", "från"]),
        ("norwegian", ["ikke", "som", "det", "til", "med", "har", "kan", "skal", "eller", "etter", "være", "denne", "fra", "ved", "også", "andre"]),
        ("finnish", ["että", "olla", "sekä", "myös", "mutta", "kuin", "tämä", "joka", "sen", "voi", "ovat", "hän", "kun", "niin", "tai", "vain"]),
        ("turkish", ["bir", "bu", "ve", "için", "ile", "olarak", "daha", "olan", "veya", "gibi", "kadar", "sonra", "göre", "ancak", "ise", "her"]),
        ("romanian", ["care", "este", "pentru", "sunt", "din", "prin", "său", "către", "dacă", "fie", "sau", "acest", "aceasta", "poate", "asupra", "între"]),
        ("hungarian", ["hogy", "nem", "egy", "meg", "vagy", "amely", "után", "által", "lehet", "kell", "való", "ezt", "mint", "illetve", "esetén", "szerint"]),
        ("indonesian", ["yang", "dan", "dengan", "untuk", "dari", "pada", "ini", "atau", "tidak", "dalam", "adalah", "akan", "oleh", "dapat", "telah", "juga"]),
        ("catalan", ["els", "amb", "aquest", "aquesta", "però", "seva", "també", "aquestes", "quan", "sense", "fins", "pot", "han", "molt", "cada", "altres"]),
    ];

    private static readonly Dictionary<string, List<string>> WordToLanguages = BuildIndex();

    /// <summary>
    /// The configuration to index <paramref name="text"/> with, honouring an explicit override,
    /// then detection, then the collection's declared default.
    /// </summary>
    public static string Detect(string? text, string? explicitLanguage = null, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitLanguage))
        {
            return RagLanguage.Normalize(explicitLanguage);
        }

        var detected = DetectCore(text);
        return detected ?? RagLanguage.Normalize(fallback);
    }

    /// <summary>The detected configuration, or null when the evidence does not support a guess.</summary>
    public static string? DetectCore(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < MinimumCharacters)
        {
            return null;
        }

        var sample = text.Length > SampleCharacters ? text[..SampleCharacters] : text;

        if (DetectByScript(sample) is { } byScript)
        {
            return byScript;
        }

        // Each hit is worth 1/(languages sharing that word). Without this the Romance languages are
        // indistinguishable — "que", "para" and "com" are common to several, so an unweighted vote
        // splits almost evenly and the margin rule declines on text a reader would find obvious.
        // Weighting lets the words that actually discriminate ("não", "dos") decide.
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var tokens = 0;
        foreach (var token in Tokenize(sample))
        {
            tokens++;
            if (WordToLanguages.TryGetValue(token, out var languages))
            {
                var weight = 1.0 / languages.Count;
                foreach (var language in languages)
                {
                    scores[language] = scores.GetValueOrDefault(language) + weight;
                }
            }
        }

        if (tokens == 0 || scores.Count == 0)
        {
            return null;
        }

        var ranked = scores.OrderByDescending(kv => kv.Value).ToList();
        var (winner, topScore) = (ranked[0].Key, ranked[0].Value);
        var runnerUp = ranked.Count > 1 ? ranked[1].Value : 0d;

        // Decline when the lead is narrow (the sets overlap across Romance languages) or when
        // barely any token matched at all — either way "simple" beats a confident wrong stemmer.
        if (topScore < runnerUp * WinMargin || topScore / tokens < MinimumHitRate)
        {
            return null;
        }

        return RagLanguage.Supported.Contains(winner) ? winner : null;
    }

    /// <summary>
    /// Script is decisive where it applies: a Cyrillic document is not English, whatever its
    /// function words look like. CJK deliberately returns "simple" — Postgres has no CJK segmenter,
    /// so pretending otherwise would produce one giant token; the vector arm carries those corpora.
    /// </summary>
    private static string? DetectByScript(string sample)
    {
        int cyrillic = 0, greek = 0, arabic = 0, cjk = 0, letters = 0;

        foreach (var ch in sample)
        {
            if (!char.IsLetter(ch))
            {
                continue;
            }

            letters++;
            switch (ch)
            {
                case >= 'Ѐ' and <= 'ӿ':
                    cyrillic++;
                    break;
                case >= 'Ͱ' and <= 'Ͽ':
                    greek++;
                    break;
                case >= '؀' and <= 'ۿ':
                    arabic++;
                    break;
                case >= '一' and <= '鿿' or >= '぀' and <= 'ヿ' or >= '가' and <= '힯':
                    cjk++;
                    break;
            }
        }

        if (letters == 0)
        {
            return null;
        }

        const double ScriptMajority = 0.3;
        if ((double)cyrillic / letters > ScriptMajority)
        {
            return "russian";
        }

        if ((double)greek / letters > ScriptMajority)
        {
            return "greek";
        }

        if ((double)arabic / letters > ScriptMajority)
        {
            return "arabic";
        }

        return (double)cjk / letters > ScriptMajority ? RagLanguage.Default : null;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isLetter = i < text.Length && char.IsLetter(text[i]);
            if (isLetter && start < 0)
            {
                start = i;
            }
            else if (!isLetter && start >= 0)
            {
                if (i - start is > 1 and <= 12)
                {
                    yield return text[start..i].ToLower(CultureInfo.InvariantCulture);
                }

                start = -1;
            }
        }
    }

    private static Dictionary<string, List<string>> BuildIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (language, words) in StopWords)
        {
            foreach (var word in words)
            {
                if (!index.TryGetValue(word, out var languages))
                {
                    index[word] = languages = [];
                }

                languages.Add(language);
            }
        }

        return index;
    }
}

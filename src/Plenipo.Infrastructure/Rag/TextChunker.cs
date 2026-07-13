namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// Structure-aware-enough chunking: paragraphs are packed whole up to the size target, and only a
/// paragraph longer than the target is split at sentence boundaries. Simple beats exotic here —
/// the 2025/26 chunking benchmarks disagree with each other, but all agree boundaries should
/// follow document structure and every chunk needs provenance (which the caller stamps).
/// </summary>
public static class TextChunker
{
    public static IReadOnlyList<string> Chunk(string text, int maxChars)
    {
        var paragraphs = text
            .Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .SelectMany(p => p.Length <= maxChars ? [p] : SplitLong(p, maxChars));

        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > maxChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(paragraph);
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    private static IEnumerable<string> SplitLong(string paragraph, int maxChars)
    {
        var current = new System.Text.StringBuilder();
        foreach (var sentence in SplitSentences(paragraph))
        {
            if (current.Length > 0 && current.Length + sentence.Length + 1 > maxChars)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            // A single sentence longer than the target gets hard-wrapped — nothing better exists.
            if (sentence.Length > maxChars)
            {
                foreach (var piece in HardWrap(sentence, maxChars))
                {
                    yield return piece;
                }
            }
            else
            {
                current.Append(sentence);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                yield return text[start..(i + 1)].Trim();
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            var tail = text[start..].Trim();
            if (tail.Length > 0)
            {
                yield return tail;
            }
        }
    }

    private static IEnumerable<string> HardWrap(string text, int maxChars)
    {
        for (var i = 0; i < text.Length; i += maxChars)
        {
            yield return text.Substring(i, Math.Min(maxChars, text.Length - i));
        }
    }
}

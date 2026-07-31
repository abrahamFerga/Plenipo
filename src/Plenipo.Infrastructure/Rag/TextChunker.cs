namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// One chunk as a half-open range <c>[Start, End)</c> into the document it came from, plus that
/// slice's text. Offsets rather than just text, because they are what maps a passage back to a page
/// — provenance that a citation can name.
/// </summary>
public sealed record TextChunk(string Text, int Start, int End);

/// <summary>
/// Structure-aware-enough chunking: paragraphs are packed whole up to the size target, and only a
/// paragraph longer than the target is split at sentence boundaries. Simple beats exotic here —
/// the 2025/26 chunking benchmarks disagree with each other, but all agree boundaries should
/// follow document structure and every chunk needs provenance.
/// <para>
/// Every chunk is a CONTIGUOUS SLICE of the input, never a reassembly. That keeps the source text
/// exactly as written (separators included) and makes the offsets trivially correct, which is what
/// page attribution depends on — a chunk stitched together from non-adjacent pieces could not
/// honestly claim a page range.
/// </para>
/// </summary>
public static class TextChunker
{
    public static IReadOnlyList<TextChunk> Chunk(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "Chunk size must be positive.");
        }

        var chunks = new List<TextChunk>();
        int start = -1, end = -1;

        void Flush()
        {
            if (start >= 0)
            {
                chunks.Add(new TextChunk(text[start..end], start, end));
                start = -1;
            }
        }

        foreach (var (pStart, pEnd) in Paragraphs(text))
        {
            if (pEnd - pStart > maxChars)
            {
                Flush();
                foreach (var (sStart, sEnd) in SplitLong(text, pStart, pEnd, maxChars))
                {
                    chunks.Add(new TextChunk(text[sStart..sEnd], sStart, sEnd));
                }

                continue;
            }

            if (start < 0)
            {
                (start, end) = (pStart, pEnd);
            }
            else if (pEnd - start > maxChars)
            {
                // Measured against the SLICE, not the sum of paragraph lengths: the separators
                // between them are part of what gets stored, so they count toward the budget.
                Flush();
                (start, end) = (pStart, pEnd);
            }
            else
            {
                end = pEnd;
            }
        }

        Flush();
        return chunks;
    }

    /// <summary>
    /// Paragraph ranges, trimmed and non-empty. A paragraph break is a run of whitespace containing
    /// at least two line breaks, so both <c>\n\n</c> and <c>\r\n\r\n</c> work without normalising the
    /// input — normalising would shift every offset the caller is relying on.
    /// </summary>
    private static IEnumerable<(int Start, int End)> Paragraphs(string text)
    {
        var cursor = 0;
        while (cursor < text.Length)
        {
            var breakStart = FindParagraphBreak(text, cursor, out var breakEnd);
            var (start, end) = Trim(text, cursor, breakStart < 0 ? text.Length : breakStart);
            if (end > start)
            {
                yield return (start, end);
            }

            if (breakStart < 0)
            {
                yield break;
            }

            cursor = breakEnd;
        }
    }

    /// <summary>Index of the next paragraph break at or after <paramref name="from"/>, or -1.</summary>
    private static int FindParagraphBreak(string text, int from, out int breakEnd)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] is not '\n')
            {
                continue;
            }

            // Walk the whitespace run this newline belongs to and count its line breaks.
            var runStart = i;
            while (runStart > from && char.IsWhiteSpace(text[runStart - 1]))
            {
                runStart--;
            }

            var runEnd = i;
            var newlines = 0;
            while (runEnd < text.Length && char.IsWhiteSpace(text[runEnd]))
            {
                if (text[runEnd] == '\n')
                {
                    newlines++;
                }

                runEnd++;
            }

            if (newlines >= 2)
            {
                breakEnd = runEnd;
                return runStart;
            }

            i = runEnd - 1; // skip the run we just measured
        }

        breakEnd = text.Length;
        return -1;
    }

    /// <summary>
    /// Splits an over-long paragraph at sentence boundaries, then hard-wraps a sentence that is
    /// itself longer than the target — nothing better exists for a wall of text with no punctuation.
    /// </summary>
    private static IEnumerable<(int Start, int End)> SplitLong(string text, int from, int to, int maxChars)
    {
        int start = -1, end = -1;

        foreach (var (sStart, sEnd) in Sentences(text, from, to))
        {
            if (sEnd - sStart > maxChars)
            {
                if (start >= 0)
                {
                    yield return (start, end);
                    start = -1;
                }

                for (var i = sStart; i < sEnd; i += maxChars)
                {
                    yield return (i, Math.Min(i + maxChars, sEnd));
                }

                continue;
            }

            if (start < 0)
            {
                (start, end) = (sStart, sEnd);
            }
            else if (sEnd - start > maxChars)
            {
                yield return (start, end);
                (start, end) = (sStart, sEnd);
            }
            else
            {
                end = sEnd;
            }
        }

        if (start >= 0)
        {
            yield return (start, end);
        }
    }

    private static IEnumerable<(int Start, int End)> Sentences(string text, int from, int to)
    {
        var cursor = from;
        for (var i = from; i < to; i++)
        {
            if (text[i] is '.' or '!' or '?' && (i + 1 == to || char.IsWhiteSpace(text[i + 1])))
            {
                var (start, end) = Trim(text, cursor, i + 1);
                if (end > start)
                {
                    yield return (start, end);
                }

                cursor = i + 1;
            }
        }

        var (tailStart, tailEnd) = Trim(text, cursor, to);
        if (tailEnd > tailStart)
        {
            yield return (tailStart, tailEnd);
        }
    }

    private static (int Start, int End) Trim(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return (start, end);
    }
}

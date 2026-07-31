namespace Plenipo.Application.Documents;

/// <summary>
/// One page of an extracted document, as a half-open character range <c>[Start, End)</c> into the
/// extracted text. Ranges rather than per-page strings, so a consumer that chunks across page
/// breaks (retrieval does) can still say which pages a passage covers.
/// </summary>
public sealed record DocumentPage(int Number, int Start, int End);

/// <summary>
/// Extracted text plus, when the source is paginated and the extractor knows where the breaks are,
/// the page boundaries within it. <see cref="Pages"/> is empty for sources that have no pages (plain
/// text) or extractors that cannot report them — callers must treat page information as optional
/// rather than assuming every document has it.
/// </summary>
public sealed record DocumentText(string Text, IReadOnlyList<DocumentPage> Pages)
{
    /// <summary>Text with no page information — the honest result for a non-paginated source.</summary>
    public static DocumentText Unpaged(string text) => new(text, []);

    /// <summary>
    /// The 1-based page numbers a character range falls on, or <c>(null, null)</c> when this document
    /// has no page information. A range spanning a page break reports both ends, which is what makes
    /// "pp. 3–4" possible for a passage that straddles one.
    /// </summary>
    public (int? From, int? To) PagesFor(int start, int end)
    {
        if (Pages.Count == 0)
        {
            return (null, null);
        }

        int? from = null;
        int? to = null;
        foreach (var page in Pages)
        {
            // Half-open overlap: a chunk ending exactly at a page boundary belongs to the page it
            // came from, not the one it stops at.
            if (page.Start < end && start < page.End)
            {
                from ??= page.Number;
                to = page.Number;
            }
        }

        // A range past the last recorded page (extraction truncation, trailing whitespace) still
        // gets attributed to the last page rather than losing its citation.
        if (from is null)
        {
            var last = Pages[^1];
            return start >= last.End ? (last.Number, last.Number) : (null, null);
        }

        return (from, to);
    }
}

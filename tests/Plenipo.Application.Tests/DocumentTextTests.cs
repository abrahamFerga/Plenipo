using Plenipo.Application.Documents;
using Plenipo.Application.Rag;

namespace Plenipo.Application.Tests;

/// <summary>
/// Mapping a passage's character range onto page numbers. The rule that matters is that a page is
/// never invented: an unpaginated source, or a range that cannot be placed, cites the file alone.
/// </summary>
public sealed class DocumentTextTests
{
    // Three pages of 10 characters each: [0,10) [10,20) [20,30).
    private static readonly DocumentText ThreePages = new(
        new string('x', 30),
        [new DocumentPage(1, 0, 10), new DocumentPage(2, 10, 20), new DocumentPage(3, 20, 30)]);

    [Theory]
    [InlineData(0, 10, 1, 1)]    // exactly page 1
    [InlineData(2, 8, 1, 1)]     // inside page 1
    [InlineData(10, 20, 2, 2)]   // exactly page 2
    [InlineData(8, 12, 1, 2)]    // straddles the 1/2 break
    [InlineData(5, 25, 1, 3)]    // spans all three
    [InlineData(20, 30, 3, 3)]   // the last page
    public void Maps_a_range_onto_the_pages_it_covers(int start, int end, int expectedFrom, int expectedTo)
    {
        Assert.Equal((expectedFrom, expectedTo), ThreePages.PagesFor(start, end));
    }

    [Fact]
    public void A_range_ending_exactly_on_a_boundary_belongs_to_the_page_it_came_from()
    {
        // Half-open: [0,10) is page 1 only. Counting page 2 here would over-cite every chunk that
        // happens to end at a page break.
        Assert.Equal((1, 1), ThreePages.PagesFor(0, 10));
        Assert.Equal((1, 2), ThreePages.PagesFor(0, 11));
    }

    [Fact]
    public void An_unpaginated_document_reports_no_pages()
    {
        var plain = DocumentText.Unpaged("some text with no pages at all");

        Assert.Empty(plain.Pages);
        Assert.Equal((null, null), plain.PagesFor(0, 10));
    }

    [Fact]
    public void A_range_past_the_last_page_is_attributed_to_the_last_page()
    {
        // Trailing whitespace or a truncated extraction can leave a chunk beyond the recorded
        // pages. Attributing it to the last page beats dropping the citation.
        Assert.Equal((3, 3), ThreePages.PagesFor(30, 34));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(4, 4, "p. 4")]
    [InlineData(4, null, "p. 4")]
    [InlineData(3, 4, "pp. 3–4")]
    [InlineData(1, 12, "pp. 1–12")]
    public void Formats_the_citation_the_way_a_reader_expects(int? from, int? to, string? expected)
    {
        var hit = new RagHit(
            Guid.Empty, Guid.Empty, "c", Guid.Empty, "f.pdf", 0, "text", 0.5, from, to);

        Assert.Equal(expected, hit.PageCitation);
    }
}

using Plenipo.Infrastructure.Documents;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// The generate→extract roundtrip must preserve LINE structure. PdfPig's <c>page.Text</c>
/// concatenates every glyph run with no separators — a statement's "date description amount"
/// rows would collapse into one blob, breaking every line-oriented consumer (statement import
/// in finance products, RAG chunking, read_document). Reading-order extraction keeps the rows.
/// </summary>
public class PdfTextExtractionTests
{
    [Fact]
    public void Extraction_preserves_line_structure_of_generated_pdfs()
    {
        var body = string.Join("\n\n",
            "2026-07-01 WHOLE FOODS MARKET 82.45",
            "2026-07-03 ACME PAYROLL DEPOSIT 2,500.00",
            "2026-07-05 SHELL GASOLINE 41.20");
        var bytes = DocumentTools.BuildPdf("Checking statement", body);

        using var stream = new MemoryStream(bytes);
        var text = DocumentTools.ExtractPdfText(stream);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("2026-07-01 WHOLE FOODS MARKET 82.45", lines);
        Assert.Contains("2026-07-03 ACME PAYROLL DEPOSIT 2,500.00", lines);
        Assert.Contains("2026-07-05 SHELL GASOLINE 41.20", lines);
    }

    [Fact]
    public void Extraction_reads_multiple_pages()
    {
        // ~90 paragraphs paginate past one A4 page; every row must still come back on its own line.
        var rows = Enumerable.Range(1, 90).Select(i => $"2026-06-{(i % 28) + 1:D2} MERCHANT {i} {i}.00");
        var bytes = DocumentTools.BuildPdf("Long statement", string.Join("\n\n", rows));

        using var stream = new MemoryStream(bytes);
        var text = DocumentTools.ExtractPdfText(stream);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("2026-06-02 MERCHANT 1 1.00", lines);
        Assert.Contains("2026-06-07 MERCHANT 90 90.00", lines); // 90 % 28 + 1 = 7
    }
}

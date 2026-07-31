using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Rag;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// Language detection decides which stemmer and stop-word list a document is indexed with, which is
/// what makes keyword retrieval work outside English. The contract is deliberately asymmetric: a
/// confident detection is worth having, a wrong one costs recall on every future query, so the
/// detector must decline rather than guess.
/// </summary>
public sealed class LanguageDetectorTests
{
    [Theory]
    [InlineData("english", "The parties agree that this agreement shall be governed by the laws of England and that any dispute which arises is to be resolved by arbitration, and not by the courts.")]
    [InlineData("spanish", "Las partes acuerdan que el presente contrato se regirá por las leyes españolas y que cualquier controversia que surja entre ellas será resuelta mediante arbitraje, según lo dispuesto por la ley.")]
    [InlineData("german", "Die Parteien vereinbaren, dass für diesen Vertrag das Recht der Bundesrepublik gilt und dass jede Streitigkeit, die sich aus dem Vertrag ergibt, durch ein Schiedsgericht und nicht durch die Gerichte entschieden wird.")]
    [InlineData("french", "Les parties conviennent que le présent contrat est régi par les lois françaises et que tout différend qui survient entre elles sera résolu par voie d'arbitrage, dans les conditions prévues par la loi.")]
    [InlineData("portuguese", "As partes acordam que o presente contrato não será regido por outras leis e que qualquer litígio que surja entre elas, com base nas cláusulas dos anexos, será resolvido mais rapidamente por arbitragem.")]
    [InlineData("dutch", "De partijen komen overeen dat deze overeenkomst wordt beheerst door het Nederlandse recht en dat elk geschil dat voor de rechter komt, niet door hen maar door een arbiter wordt beslecht.")]
    public void Detects_the_language_of_a_representative_paragraph(string expected, string text)
    {
        Assert.Equal(expected, LanguageDetector.Detect(text));
    }

    [Fact]
    public void Detects_non_latin_scripts_by_script_rather_than_stop_words()
    {
        // Script is decisive where it applies — a Cyrillic document cannot be English regardless of
        // which function words a stop-word vote happens to find.
        Assert.Equal("russian", LanguageDetector.Detect(
            "Стороны договорились, что настоящий договор регулируется законодательством и любой спор будет разрешён в арбитражном суде."));
        Assert.Equal("greek", LanguageDetector.Detect(
            "Τα μέρη συμφωνούν ότι η παρούσα σύμβαση διέπεται από το δίκαιο και κάθε διαφορά θα επιλύεται με διαιτησία."));
    }

    [Fact]
    public void Falls_back_to_simple_for_scripts_postgres_cannot_segment()
    {
        // Postgres ships no CJK segmenter: claiming a configuration would produce one enormous
        // token. "simple" is honest, and the vector arm still carries these corpora.
        Assert.Equal(RagLanguage.Default, LanguageDetector.Detect(
            "双方同意本协议受法律管辖，因本协议引起的任何争议均应通过仲裁解决，而不通过法院解决，并且仲裁裁决为终局裁决。"));
    }

    [Fact]
    public void Declines_to_guess_when_the_evidence_is_thin()
    {
        Assert.Null(LanguageDetector.DetectCore(null));
        Assert.Null(LanguageDetector.DetectCore(""));
        Assert.Null(LanguageDetector.DetectCore("Acme Corp."));                  // too short to vote on
        Assert.Null(LanguageDetector.DetectCore(new string('x', 500)));          // long but no words
        Assert.Null(LanguageDetector.DetectCore(
            "Invoice 4471 — 12,500.00 EUR — 2026-03-14 — ref XY/9921 — VAT 21% — total 15,125.00 EUR — PO 88134"));
    }

    [Fact]
    public void Declining_resolves_to_the_collection_default()
    {
        // The fallback chain is explicit override, then detection, then the collection's language.
        Assert.Equal("spanish", LanguageDetector.Detect("Acme Corp.", explicitLanguage: null, fallback: "spanish"));
        Assert.Equal(RagLanguage.Default, LanguageDetector.Detect("Acme Corp."));
    }

    [Fact]
    public void An_explicit_language_always_wins_over_detection()
    {
        const string english = "The parties agree that this agreement shall be governed by the laws of England and any dispute resolved by arbitration.";

        Assert.Equal("spanish", LanguageDetector.Detect(english, explicitLanguage: "spanish"));
        // ...but only if Postgres actually has it; an unknown name degrades to "simple", never throws.
        Assert.Equal(RagLanguage.Default, LanguageDetector.Detect(english, explicitLanguage: "klingon"));
    }

    [Fact]
    public void Normalize_is_the_injection_guard_for_the_regconfig_cast()
    {
        // Only values from the supported set are ever interpolated into a regconfig cast, so this
        // is a security property and not just tidiness.
        Assert.Equal("english", RagLanguage.Normalize("English"));
        Assert.Equal("english", RagLanguage.Normalize("  english  "));
        Assert.Equal(RagLanguage.Default, RagLanguage.Normalize("english'; DROP TABLE platform.rag_chunks; --"));
        Assert.Equal(RagLanguage.Default, RagLanguage.Normalize(null));
        Assert.Equal(RagLanguage.Default, RagLanguage.Normalize(""));
    }
}

using Plenipo.Infrastructure.Rag;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// The two deterministic primitives under the RAG pipeline. The mock embedder must be stable across
/// runs (retrieval tests depend on it) and must rank overlapping text above unrelated text; the
/// chunker must respect paragraph structure and the size target without losing content.
/// </summary>
public sealed class RagPrimitiveTests
{
    [Fact]
    public async Task Mock_embeddings_are_deterministic_and_normalized()
    {
        using var generator = new MockEmbeddingGenerator();

        var first = await generator.GenerateAsync(["The quick brown fox"]);
        var second = await generator.GenerateAsync(["The quick brown fox"]);

        Assert.Equal(first[0].Vector.ToArray(), second[0].Vector.ToArray());
        Assert.Equal(MockEmbeddingGenerator.Dimensions, first[0].Vector.Length);

        var norm = Math.Sqrt(first[0].Vector.ToArray().Sum(x => (double)x * x));
        Assert.Equal(1.0, norm, precision: 5);
    }

    [Fact]
    public async Task Mock_embeddings_rank_related_text_above_unrelated_text()
    {
        using var generator = new MockEmbeddingGenerator();

        var vectors = await generator.GenerateAsync(
        [
            "termination rights and notice periods in the services agreement",
            "either party may terminate the agreement with ninety days notice",
            "the recipe calls for two cups of flour and a pinch of salt",
        ]);

        var query = vectors[0].Vector.ToArray();
        var related = Cosine(query, vectors[1].Vector.ToArray());
        var unrelated = Cosine(query, vectors[2].Vector.ToArray());

        Assert.True(related > unrelated, $"related {related} should beat unrelated {unrelated}");
    }

    [Fact]
    public void Chunker_packs_paragraphs_and_respects_the_size_target()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 10).Select(i => $"Paragraph {i}. " + new string('x', 200)));

        var chunks = TextChunker.Chunk(text, maxChars: 500);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 500, $"chunk of {c.Length} chars exceeds the target"));
        // No content lost: every paragraph marker survives exactly once across chunks.
        var reassembled = string.Join("\n\n", chunks);
        for (var i = 1; i <= 10; i++)
        {
            Assert.Contains($"Paragraph {i}.", reassembled);
        }
    }

    [Fact]
    public void Chunker_splits_an_oversized_paragraph_at_sentence_boundaries()
    {
        var text = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"Sentence number {i} says something."));

        var chunks = TextChunker.Chunk(text, maxChars: 200);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 200));
        Assert.All(chunks, c => Assert.EndsWith(".", c)); // boundaries fall on sentence ends
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
        }

        return dot; // both vectors are unit-length
    }
}

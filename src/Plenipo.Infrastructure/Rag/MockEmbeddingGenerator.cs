using Microsoft.Extensions.AI;

namespace Plenipo.Infrastructure.Rag;

/// <summary>
/// A deterministic, dependency-free embedder — the retrieval counterpart of the Mock chat provider.
/// Bag-of-words feature hashing: each word lands in a stable bucket (FNV-1a, so identical across
/// runs and machines), vectors are L2-normalized, and cosine similarity therefore tracks word
/// overlap. That is real enough for the hybrid pipeline to rank correctly in dev/CI with no API
/// key, and completely wrong for production — configure a real provider there.
/// </summary>
public sealed class MockEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ModelId = "mock-bow-384";
    public const int Dimensions = 384;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(v => new Embedding<float>(Embed(v)) { ModelId = ModelId });
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings.ToList()));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (var word in Tokenize(text))
        {
            var hash = Fnv1a(word);
            var bucket = (int)(hash % Dimensions);
            // A second hash bit gives each word a sign, spreading mass over the sphere.
            vector[bucket] += (hash & 0x80000000) == 0 ? 1f : -1f;
        }

        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var word = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    /// <summary>Stable 32-bit FNV-1a — string.GetHashCode is randomized per process, this is not.</summary>
    private static uint Fnv1a(string word)
    {
        var hash = 2166136261u;
        foreach (var c in word)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }
}

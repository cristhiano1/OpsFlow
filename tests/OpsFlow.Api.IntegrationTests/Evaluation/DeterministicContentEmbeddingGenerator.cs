using System.Security.Cryptography;
using System.Text;
using OpsFlow.Application.Documents;

namespace OpsFlow.Api.IntegrationTests.Evaluation;

/// <summary>
/// Test-support only. A deterministic, cross-platform content embedding used by
/// the retrieval baseline. It maps text to a fixed-dimension vector via a
/// hashed-token term-frequency scheme: tokens are lowercased and split on
/// non-alphanumeric runs, each token is hashed with SHA-256 into a stable bucket
/// in <c>[0, dimensions)</c>, term frequencies accumulate, and the vector is
/// L2-normalized.
///
/// <para>
/// The <see cref="Embed(string)"/> function is the SINGLE source of vectors for
/// both the seeded corpus embeddings and the query embedding, so the semantic
/// channel is meaningful and reproducible. It uses only SHA-256, UTF-8 encoding,
/// and invariant lowercasing — never <c>GetHashCode</c>, <c>Random</c>, GUIDs,
/// or culture-dependent operations — so the same input yields the same vector
/// across processes and machines. It is NOT a real embedding model and does not
/// measure real-world semantic quality (see ADR-008).
/// </para>
/// </summary>
internal sealed class DeterministicContentEmbeddingGenerator : IEmbeddingGenerator
{
    public EmbeddingGeneratorIdentity Identity { get; } = new(
        EmbeddingProfiles.SemanticV1Id,
        EmbeddingProfiles.SemanticV1ModelId,
        EmbeddingProfiles.SemanticV1Dimensions);

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        IReadOnlyList<ReadOnlyMemory<float>> result =
            [.. texts.Select(text => (ReadOnlyMemory<float>)Embed(text))];

        return Task.FromResult(result);
    }

    /// <summary>
    /// Produces the deterministic embedding vector for a text. Shared by corpus
    /// seeding and query embedding so both live in the same vector space.
    /// </summary>
    public static float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var vector = new float[EmbeddingProfiles.SemanticV1Dimensions];
        foreach (var token in Tokenize(text))
        {
            int bucket = BucketFor(token);
            vector[bucket] += 1f;
        }

        L2Normalize(vector);
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var builder = new StringBuilder();
        foreach (var rune in text.ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString());
            }
            else if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static int BucketFor(string token)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        uint value = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
        return (int)(value % EmbeddingProfiles.SemanticV1Dimensions);
    }

    private static void L2Normalize(float[] vector)
    {
        double sumOfSquares = 0.0;
        foreach (float component in vector)
        {
            sumOfSquares += component * (double)component;
        }

        if (sumOfSquares <= 0.0)
        {
            return;
        }

        double norm = Math.Sqrt(sumOfSquares);
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
    }
}

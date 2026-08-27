namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral embedding generation port. Implementations reside in
/// Infrastructure. The generator identity is immutable for a given DI
/// registration and is validated at startup.
/// </summary>
public interface IEmbeddingGenerator
{
    /// <summary>Immutable identity: profile, model, and dimensions.</summary>
    EmbeddingGeneratorIdentity Identity { get; }

    /// <summary>
    /// Generates embeddings for a batch of texts in a single provider call.
    /// Result order matches input order: <c>result[i]</c> corresponds to <c>texts[i]</c>.
    /// </summary>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);
}

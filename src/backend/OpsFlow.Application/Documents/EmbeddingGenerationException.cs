namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral exception representing a failure in the embedding
/// generation boundary. Infrastructure adapters wrap provider-specific
/// exceptions in this type so callers never depend on provider SDK types.
/// </summary>
public sealed class EmbeddingGenerationException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public EmbeddingGenerationException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public EmbeddingGenerationException(string message, Exception innerException)
        : base(message, innerException) { }
}

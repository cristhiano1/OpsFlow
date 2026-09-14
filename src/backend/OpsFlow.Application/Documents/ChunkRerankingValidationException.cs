namespace OpsFlow.Application.Documents;

/// <summary>
/// Signals that a reranker returned a response that violates the reranking
/// contract: a null score collection, a null score entry, a wrong score count,
/// an unknown, duplicate, or missing candidate score, or a non-finite score. The
/// reranker's output is treated as untrusted; violations fail closed rather than
/// being silently repaired. Distinct from <see cref="ChunkRerankingException"/>,
/// which covers provider/transport availability failures.
/// </summary>
public sealed class ChunkRerankingValidationException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public ChunkRerankingValidationException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ChunkRerankingValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public ChunkRerankingValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

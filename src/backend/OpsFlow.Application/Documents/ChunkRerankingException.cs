namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral exception representing a failure in the reranking boundary:
/// provider/network unavailability, transport/SDK errors, or a provider that
/// cannot service the request (for example a candidate count exceeding its
/// capacity). Infrastructure adapters wrap provider-specific failures in this
/// type so callers never depend on provider SDK types. Distinct from
/// <see cref="ChunkRerankingValidationException"/>, which signals a well-formed
/// call whose response violated the reranking contract.
/// </summary>
public sealed class ChunkRerankingException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public ChunkRerankingException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ChunkRerankingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public ChunkRerankingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

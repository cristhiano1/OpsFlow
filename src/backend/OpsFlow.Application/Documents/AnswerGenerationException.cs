namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral exception representing a failure in the answer-generation
/// boundary. Infrastructure adapters wrap provider-specific failures (network,
/// SDK, invalid or truncated provider responses, refusals) in this type so
/// callers never depend on provider SDK types. This is distinct from
/// <see cref="GroundedAnswerValidationException"/>, which signals that a
/// well-formed response violated the grounding contract.
/// </summary>
public sealed class AnswerGenerationException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public AnswerGenerationException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public AnswerGenerationException(string message, Exception innerException)
        : base(message, innerException) { }
}

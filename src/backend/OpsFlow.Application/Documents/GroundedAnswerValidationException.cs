namespace OpsFlow.Application.Documents;

/// <summary>
/// Thrown when a well-formed generator output violates the grounding contract —
/// for example an answered result with no citations, a citation label outside
/// the supplied evidence range, or an insufficient-evidence result that
/// nonetheless carries answer text or citations. The Application layer fails
/// closed rather than surfacing an unverified answer. This is distinct from
/// <see cref="AnswerGenerationException"/>, which signals a provider/transport
/// failure.
/// </summary>
public sealed class GroundedAnswerValidationException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public GroundedAnswerValidationException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public GroundedAnswerValidationException(string message, Exception innerException)
        : base(message, innerException) { }
}

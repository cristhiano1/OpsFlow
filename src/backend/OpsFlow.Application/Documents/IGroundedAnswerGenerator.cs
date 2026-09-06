namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral port for grounded answer generation. Implementations reside
/// in Infrastructure and translate the neutral request into a provider call,
/// returning a neutral <see cref="GroundedAnswerGenerationOutput"/>. The
/// Application layer owns the grounding policy and prompt construction; the
/// implementation owns only the provider interaction and response parsing.
/// </summary>
public interface IGroundedAnswerGenerator
{
    /// <summary>
    /// Generates a structured answer for the supplied prompts. The returned
    /// citation numbers are the raw, model-declared labels — they are validated
    /// and mapped to retrieved evidence by the Application layer, never trusted
    /// as authoritative metadata.
    /// </summary>
    Task<GroundedAnswerGenerationOutput> GenerateAsync(
        GroundedAnswerGenerationRequest request,
        CancellationToken cancellationToken);
}

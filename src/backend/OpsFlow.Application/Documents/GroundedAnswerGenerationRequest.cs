namespace OpsFlow.Application.Documents;

/// <summary>
/// Provider-neutral input to <see cref="IGroundedAnswerGenerator"/>. The
/// Application layer builds both prompts because grounding and citation
/// semantics are product concerns; the implementation only maps them to the
/// provider's message roles and requests structured output.
/// </summary>
/// <param name="SystemPrompt">Static grounding policy (system role).</param>
/// <param name="UserPrompt">Escaped question and labeled evidence (user role).</param>
public sealed record GroundedAnswerGenerationRequest(
    string SystemPrompt,
    string UserPrompt);

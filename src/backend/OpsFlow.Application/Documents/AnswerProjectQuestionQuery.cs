namespace OpsFlow.Application.Documents;

/// <summary>
/// Input for the grounded answer-generation use case. <c>OrganizationId</c> is
/// the authenticated caller's tenant (never accepted from a client body).
/// The evidence retrieval count is a fixed internal constant and is
/// intentionally not exposed here.
/// </summary>
/// <param name="OrganizationId">The authenticated caller's organization.</param>
/// <param name="ProjectId">The project to answer the question within.</param>
/// <param name="Question">Natural-language question, forwarded unchanged to hybrid retrieval.</param>
public sealed record AnswerProjectQuestionQuery(
    Guid OrganizationId,
    Guid ProjectId,
    string Question);

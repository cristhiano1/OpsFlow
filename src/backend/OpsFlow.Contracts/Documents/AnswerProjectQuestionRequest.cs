namespace OpsFlow.Contracts.Documents;

/// <summary>
/// The public request body for asking a grounded question about a project's
/// documents. <c>OrganizationId</c> is intentionally absent — the tenant
/// identity is extracted exclusively from the authenticated JWT — and
/// <c>ProjectId</c> is encoded in the request URL. The evidence retrieval count,
/// model, and prompt are fixed internal concerns and are never client-supplied.
/// </summary>
/// <param name="Question">Natural-language question, forwarded unchanged to grounded retrieval and answering.</param>
public sealed record AnswerProjectQuestionRequest(string? Question);

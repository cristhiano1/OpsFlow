namespace OpsFlow.Application.Projects;

/// <summary>
/// Input to the list-projects use case. <c>OrganizationId</c> is the trusted
/// tenant identity extracted from the caller's JWT.
/// </summary>
/// <param name="OrganizationId">The authenticated caller's organization.</param>
public sealed record ListProjectsQuery(Guid OrganizationId);

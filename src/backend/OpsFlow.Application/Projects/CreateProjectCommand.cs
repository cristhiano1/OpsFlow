namespace OpsFlow.Application.Projects;

/// <summary>
/// Input to the create-project use case. <c>OrganizationId</c> is the trusted
/// tenant identity extracted from the caller's JWT — it is never supplied by
/// the client request body.
/// </summary>
/// <param name="OrganizationId">The authenticated caller's organization.</param>
/// <param name="Name">The project name as entered by the user (pre-normalization).</param>
/// <param name="Description">The optional project description (pre-normalization).</param>
public sealed record CreateProjectCommand(Guid OrganizationId, string? Name, string? Description);

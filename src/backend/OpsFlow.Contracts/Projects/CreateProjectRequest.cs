namespace OpsFlow.Contracts.Projects;

/// <summary>
/// The public request body for creating a project. Contains only user-supplied
/// data; <c>OrganizationId</c> is intentionally absent because the tenant
/// identity is extracted exclusively from the authenticated JWT.
/// </summary>
/// <param name="Name">The project name.</param>
/// <param name="Description">An optional project description.</param>
public sealed record CreateProjectRequest(string? Name, string? Description);

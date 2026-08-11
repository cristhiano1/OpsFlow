namespace OpsFlow.Contracts.Projects;

/// <summary>
/// A safe projection of a project. Does not expose <c>OrganizationId</c>
/// because the caller already knows their own tenant.
/// </summary>
/// <param name="Id">The project's unique identifier.</param>
/// <param name="Name">The project name.</param>
/// <param name="Description">The optional project description.</param>
/// <param name="CreatedAt">The UTC timestamp when the project was created.</param>
public sealed record ProjectResponse(
    Guid Id,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt);

namespace OpsFlow.Contracts.Projects;

/// <summary>
/// Stable API envelope for a collection of projects.
/// </summary>
/// <param name="Items">The projects belonging to the authenticated caller's organization.</param>
public sealed record ProjectListResponse(IReadOnlyList<ProjectResponse> Items);

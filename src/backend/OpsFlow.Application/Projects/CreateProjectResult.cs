using OpsFlow.Domain.Projects;

namespace OpsFlow.Application.Projects;

/// <summary>
/// The result of the create-project use case. Use the <see cref="Success"/>
/// and <see cref="ValidationError"/> factory methods; the private constructor
/// prevents inconsistent combinations.
/// </summary>
public sealed record CreateProjectResult
{
    private CreateProjectResult(bool succeeded, Project? project, string? error)
    {
        Succeeded = succeeded;
        Project = project;
        Error = error;
    }

    /// <summary>Whether the project was created successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>The created project on success; <c>null</c> on failure.</summary>
    public Project? Project { get; }

    /// <summary>The validation error message on failure; <c>null</c> on success.</summary>
    public string? Error { get; }

    /// <summary>Creates a successful result with the persisted project.</summary>
    public static CreateProjectResult Success(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new CreateProjectResult(succeeded: true, project: project, error: null);
    }

    /// <summary>Creates a validation-error result.</summary>
    public static CreateProjectResult ValidationError(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new CreateProjectResult(succeeded: false, project: null, error: error);
    }
}

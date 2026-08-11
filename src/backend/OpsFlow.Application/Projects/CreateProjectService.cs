using OpsFlow.Application.Abstractions;
using OpsFlow.Domain.Projects;

namespace OpsFlow.Application.Projects;

/// <summary>
/// Coordinates the create-project use case. Validates and normalizes user
/// input, creates the domain entity, and persists it through
/// <see cref="IProjectRepository"/>. The service remains technology-neutral:
/// it references no HTTP, EF Core, or Identity type.
/// </summary>
public sealed class CreateProjectService
{
    private readonly IProjectRepository _repository;
    private readonly IClock _clock;

    /// <summary>Creates the service with its collaborators.</summary>
    public CreateProjectService(IProjectRepository repository, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(clock);

        _repository = repository;
        _clock = clock;
    }

    /// <summary>
    /// Validates, normalizes, and persists a new project. Returns an explicit
    /// success or validation-error result.
    /// </summary>
    public async Task<CreateProjectResult> CreateAsync(
        CreateProjectCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.OrganizationId == Guid.Empty)
        {
            return CreateProjectResult.ValidationError("Organization ID is required.");
        }

        var name = command.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return CreateProjectResult.ValidationError("Name is required.");
        }

        if (name.Length > Project.NameMaxLength)
        {
            return CreateProjectResult.ValidationError($"Name must not exceed {Project.NameMaxLength} characters.");
        }

        string? description = command.Description?.Trim();
        if (string.IsNullOrEmpty(description))
        {
            description = null;
        }

        if (description is not null && description.Length > Project.DescriptionMaxLength)
        {
            return CreateProjectResult.ValidationError($"Description must not exceed {Project.DescriptionMaxLength} characters.");
        }

        var project = new Project(
            id: Guid.NewGuid(),
            organizationId: command.OrganizationId,
            name: name,
            description: description,
            createdAt: _clock.UtcNow);

        await _repository.AddAsync(project, cancellationToken);

        return CreateProjectResult.Success(project);
    }
}

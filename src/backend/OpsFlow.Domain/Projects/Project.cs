namespace OpsFlow.Domain.Projects;

/// <summary>
/// An organization-scoped project. Every future business record (documents,
/// requirements, evidence) will belong to a project. This is a pure domain
/// entity with no persistence or framework dependencies.
/// </summary>
public class Project
{
    /// <summary>Maximum length for the project name.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Maximum length for the project description.</summary>
    public const int DescriptionMaxLength = 2000;

    /// <summary>Database identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Identifier of the organization this project belongs to.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Human-readable project name.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Optional project description.</summary>
    public string? Description { get; private set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    // EF Core requires a parameterless constructor.
    private Project() { }

    /// <summary>Creates a new project with validated invariants.</summary>
    public Project(Guid id, Guid organizationId, string name, string? description, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Project ID must not be empty.", nameof(id));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization ID must not be empty.", nameof(organizationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Length > NameMaxLength)
        {
            throw new ArgumentException($"Name must not exceed {NameMaxLength} characters.", nameof(name));
        }

        if (description is not null && description.Length > DescriptionMaxLength)
        {
            throw new ArgumentException($"Description must not exceed {DescriptionMaxLength} characters.", nameof(description));
        }

        Id = id;
        OrganizationId = organizationId;
        Name = name;
        Description = description;
        CreatedAt = createdAt;
    }
}

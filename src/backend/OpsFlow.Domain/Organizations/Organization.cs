namespace OpsFlow.Domain.Organizations;

/// <summary>
/// A tenant in OpsFlow. Every user — and, in later phases, every business
/// record — belongs to exactly one organization. This is a pure domain entity
/// with no persistence or framework dependencies.
/// </summary>
public class Organization
{
    /// <summary>Database identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Human-readable organization name.</summary>
    public required string Name { get; set; }

    /// <summary>Unique, URL-friendly identifier (lowercase); unique across the system.</summary>
    public required string Slug { get; set; }

    /// <summary>Whether the organization is active. Inactive organizations cannot authenticate.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last update timestamp (UTC), when applicable.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}

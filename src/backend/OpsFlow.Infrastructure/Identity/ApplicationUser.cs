using Microsoft.AspNetCore.Identity;
using OpsFlow.Domain.Organizations;

namespace OpsFlow.Infrastructure.Identity;

/// <summary>
/// Application user backed by ASP.NET Core Identity, scoped to a single
/// organization. Identity-managed fields (password hash, security stamp, etc.)
/// are inherited and are never exposed through API contracts.
/// </summary>
public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Identifier of the organization this user belongs to.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The organization this user belongs to (navigation).</summary>
    public Organization? Organization { get; set; }

    /// <summary>Display name shown in the user interface.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Whether the user is active. Inactive users cannot log in or refresh.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Timestamp of the last successful login (UTC), when available.</summary>
    public DateTimeOffset? LastLoginAt { get; set; }
}

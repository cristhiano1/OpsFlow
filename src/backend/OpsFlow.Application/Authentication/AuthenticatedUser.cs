namespace OpsFlow.Application.Authentication;

/// <summary>
/// A neutral, technology-agnostic snapshot of a successfully authenticated user.
/// It never references persistence or Identity types such as ApplicationUser.
/// The <see cref="SecurityStamp"/> is included because it is required internally
/// to detect a state change between authentication and session persistence; it
/// must never be exposed in a public API response.
/// </summary>
/// <param name="UserId">The user's unique identifier.</param>
/// <param name="Email">The user's email address.</param>
/// <param name="DisplayName">The user's display name.</param>
/// <param name="OrganizationId">The identifier of the organization the user belongs to.</param>
/// <param name="OrganizationName">The display name of the organization the user belongs to.</param>
/// <param name="SecurityStamp">The user's current security stamp, used to detect state changes.</param>
/// <param name="Roles">The role names granted to the user.</param>
public sealed record AuthenticatedUser(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid OrganizationId,
    string OrganizationName,
    string SecurityStamp,
    IReadOnlyCollection<string> Roles);

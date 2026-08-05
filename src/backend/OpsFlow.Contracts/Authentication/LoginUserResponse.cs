namespace OpsFlow.Contracts.Authentication;

/// <summary>
/// A safe, non-sensitive projection of the authenticated user included in a
/// <see cref="LoginResponse"/>. It never contains the password, password hash,
/// security stamp, refresh token, or any other secret.
/// </summary>
/// <param name="UserId">The user's unique identifier.</param>
/// <param name="Email">The user's email address.</param>
/// <param name="DisplayName">The user's display name.</param>
/// <param name="OrganizationId">The identifier of the organization the user belongs to.</param>
/// <param name="OrganizationName">The display name of the organization the user belongs to.</param>
/// <param name="Roles">The role names granted to the user.</param>
public sealed record LoginUserResponse(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid OrganizationId,
    string OrganizationName,
    IReadOnlyCollection<string> Roles);

namespace OpsFlow.Application.Authentication;

/// <summary>
/// The safe, non-sensitive view of an authenticated user carried by a successful
/// <see cref="LoginResult"/>. It deliberately excludes the security stamp,
/// refresh token, refresh-token hash, password data, lockout data, and
/// concurrency stamp.
/// </summary>
/// <param name="UserId">The user's unique identifier.</param>
/// <param name="Email">The user's email address.</param>
/// <param name="DisplayName">The user's display name.</param>
/// <param name="OrganizationId">The identifier of the organization the user belongs to.</param>
/// <param name="OrganizationName">The display name of the organization the user belongs to.</param>
/// <param name="Roles">The role names granted to the user.</param>
public sealed record LoginResultUser(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid OrganizationId,
    string OrganizationName,
    IReadOnlyCollection<string> Roles);

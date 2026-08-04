namespace OpsFlow.Application.Authentication;

/// <summary>
/// A neutral, technology-agnostic snapshot of a successfully authenticated user.
/// It never references persistence or Identity types such as ApplicationUser.
/// The <see cref="SecurityStamp"/> and <see cref="ConcurrencyStamp"/> are
/// included because they are required internally to detect a state change
/// between authentication and session persistence; neither must appear in any
/// public API response.
/// </summary>
/// <param name="UserId">The user's unique identifier.</param>
/// <param name="Email">The user's email address.</param>
/// <param name="DisplayName">The user's display name.</param>
/// <param name="OrganizationId">The identifier of the organization the user belongs to.</param>
/// <param name="OrganizationName">The display name of the organization the user belongs to.</param>
/// <param name="SecurityStamp">The user's current security stamp, used to detect state changes.</param>
/// <param name="ConcurrencyStamp">
/// The user's Identity concurrency stamp captured at authentication time. It is
/// rotated by Identity operations that flow through
/// <c>UserStore.UpdateAsync</c> (for example role, password, email, lockout
/// mutations issued via <c>UserManager</c>). The session issuer revalidates it
/// so that a stale authenticated snapshot cannot mint a valid session.
/// </param>
/// <param name="Roles">The role names granted to the user.</param>
public sealed record AuthenticatedUser(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid OrganizationId,
    string OrganizationName,
    string SecurityStamp,
    string ConcurrencyStamp,
    IReadOnlyCollection<string> Roles);

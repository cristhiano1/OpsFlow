namespace OpsFlow.Application.Authentication;

/// <summary>
/// The neutral query used by the /auth/me use case. Carries only the two
/// pieces of information the HTTP layer trusts from the presented access
/// token: the caller's user identifier and the SecurityStamp captured in the
/// JWT. The database is authoritative for every other field of the caller's
/// current profile.
/// </summary>
/// <param name="UserId">The user's identifier taken from the JWT <c>sub</c> claim.</param>
/// <param name="PresentedSecurityStamp">The <c>sstamp</c> claim value from the JWT, compared against the current DB SecurityStamp.</param>
public sealed record CurrentUserQuery(Guid UserId, string PresentedSecurityStamp);

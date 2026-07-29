namespace OpsFlow.Application.Authentication;

/// <summary>
/// The safe, technology-neutral data required to mint an access token. It
/// contains only primitives and application values and never references
/// persistence entities such as ApplicationUser.
/// </summary>
public sealed record AccessTokenDescriptor(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid OrganizationId,
    IReadOnlyCollection<string> Roles,
    string SecurityStamp);

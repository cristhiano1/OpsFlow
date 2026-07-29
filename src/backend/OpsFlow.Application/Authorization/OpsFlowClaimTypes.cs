namespace OpsFlow.Application.Authorization;

/// <summary>
/// Claim names used in OpsFlow access tokens. Combines standard JWT claim names
/// with the two OpsFlow-specific claims. Use these constants instead of raw
/// strings.
/// </summary>
public static class OpsFlowClaimTypes
{
    /// <summary>Subject claim ("sub"): the user identifier.</summary>
    public const string Subject = "sub";

    /// <summary>Email claim ("email").</summary>
    public const string Email = "email";

    /// <summary>Name claim ("name"): the user display name.</summary>
    public const string Name = "name";

    /// <summary>Role claim ("role"): emitted once per role.</summary>
    public const string Role = "role";

    /// <summary>JWT identifier claim ("jti"): unique per token.</summary>
    public const string TokenId = "jti";

    /// <summary>Organization identifier claim ("org_id"): the tenant the token belongs to.</summary>
    public const string OrganizationId = "org_id";

    /// <summary>Security-stamp claim ("sstamp"): used to invalidate old tokens after security-sensitive changes.</summary>
    public const string SecurityStamp = "sstamp";
}

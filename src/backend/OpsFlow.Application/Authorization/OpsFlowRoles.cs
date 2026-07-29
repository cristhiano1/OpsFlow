namespace OpsFlow.Application.Authorization;

/// <summary>
/// The application roles recognized by OpsFlow. Use these constants instead of
/// scattering role-name strings throughout the code.
/// </summary>
public static class OpsFlowRoles
{
    /// <summary>Full control over an organization, its users and its data.</summary>
    public const string OrganizationAdministrator = "Organization Administrator";

    /// <summary>Creates and coordinates operational work within an organization.</summary>
    public const string Coordinator = "Coordinator";

    /// <summary>Executes assigned operational work.</summary>
    public const string Technician = "Technician";

    /// <summary>Read-only access to permitted organization data.</summary>
    public const string Viewer = "Viewer";

    /// <summary>All roles, in descending order of privilege.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        OrganizationAdministrator,
        Coordinator,
        Technician,
        Viewer,
    ];

    /// <summary>Returns <c>true</c> when the supplied value is one of the four known roles.</summary>
    public static bool IsKnownRole(string role) => All.Contains(role, StringComparer.Ordinal);
}

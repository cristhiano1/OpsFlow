namespace OpsFlow.Application.Authorization;

/// <summary>
/// Named authorization policies enforced at the API boundary.
/// Each constant maps to a policy registered in
/// <c>AddOpsFlowAuthentication</c> that requires one or more
/// <see cref="OpsFlowRoles"/>.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Create or modify projects.
    /// Allowed: <see cref="OpsFlowRoles.OrganizationAdministrator"/>,
    /// <see cref="OpsFlowRoles.Coordinator"/>.
    /// </summary>
    public const string ProjectManage = "ProjectManage";

    /// <summary>
    /// Upload documents or trigger document-processing mutations
    /// (extraction, ingestion).
    /// Allowed: <see cref="OpsFlowRoles.OrganizationAdministrator"/>,
    /// <see cref="OpsFlowRoles.Coordinator"/>,
    /// <see cref="OpsFlowRoles.Technician"/>.
    /// </summary>
    public const string DocumentContribute = "DocumentContribute";
}

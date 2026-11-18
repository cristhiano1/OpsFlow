// Role names as issued by the backend (OpsFlowRoles).
export const Roles = {
  OrganizationAdministrator: 'Organization Administrator',
  Coordinator: 'Coordinator',
  Technician: 'Technician',
  Viewer: 'Viewer',
} as const

// Mirrors the backend AnalyticsRead policy. Navigation only — the API enforces access.
export function canViewAnalytics(roles: readonly string[]): boolean {
  return (
    roles.includes(Roles.OrganizationAdministrator) ||
    roles.includes(Roles.Coordinator)
  )
}

import { describe, expect, it } from 'vitest'
import { Roles, canViewAnalytics } from './roles'

describe('canViewAnalytics', () => {
  it.each([
    [Roles.OrganizationAdministrator, true],
    [Roles.Coordinator, true],
    [Roles.Technician, false],
    [Roles.Viewer, false],
  ])('%s → %s', (role, expected) => {
    expect(canViewAnalytics([role])).toBe(expected)
  })

  it('is false when the user has no roles', () => {
    expect(canViewAnalytics([])).toBe(false)
  })

  it('is true when any role grants analytics', () => {
    expect(canViewAnalytics([Roles.Viewer, Roles.Coordinator])).toBe(true)
  })

  it('does not match role names loosely', () => {
    expect(canViewAnalytics(['admin', 'coordinator', 'OrganizationAdministrator'])).toBe(false)
  })
})

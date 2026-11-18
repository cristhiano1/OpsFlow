import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('../analytics/analyticsApi', () => ({
  fetchAnalyticsSummary: vi.fn(),
}))

import { fetchAnalyticsSummary } from '../analytics/analyticsApi'
import { AuthContext } from '../auth/authContext'
import type { AuthContextValue, LoginResult } from '../auth/authContext'
import { Roles } from '../auth/roles'
import { HomePage } from './HomePage'

const mockFetch = vi.mocked(fetchAnalyticsSummary)

function renderHome(roles: readonly string[], displayName = 'Vera Viewer') {
  const value: AuthContextValue = {
    state: {
      status: 'authenticated',
      user: {
        userId: 'u1',
        email: 'user@opsflow.local',
        displayName,
        organizationId: 'org1',
        organizationName: 'Northwind Field Services',
        roles,
      },
    },
    login: vi.fn<(e: string, p: string) => Promise<LoginResult>>(),
    logout: vi.fn<() => Promise<void>>(),
    retryBootstrap: vi.fn(),
  }

  return render(
    <MemoryRouter>
      <AuthContext value={value}>
        <HomePage />
      </AuthContext>
    </MemoryRouter>,
  )
}

describe('HomePage', () => {
  beforeEach(() => {
    mockFetch.mockReset()
    mockFetch.mockReturnValue(new Promise(() => {}))
  })

  it.each([Roles.OrganizationAdministrator, Roles.Coordinator])(
    '%s loads the analytics dashboard',
    role => {
      renderHome([role])

      expect(mockFetch).toHaveBeenCalledTimes(1)
      expect(screen.getByRole('heading', { name: 'Dashboard' })).toBeInTheDocument()
    },
  )

  it.each([Roles.Viewer, Roles.Technician])(
    '%s does not call the analytics endpoint',
    role => {
      renderHome([role])

      expect(mockFetch).not.toHaveBeenCalled()
      expect(screen.queryByRole('heading', { name: 'Dashboard' })).not.toBeInTheDocument()
    },
  )

  it('Viewer gets a usable read-only landing page instead of an error', () => {
    renderHome([Roles.Viewer], 'Vera Viewer')

    expect(screen.getByRole('heading', { name: 'Welcome, Vera Viewer' })).toBeInTheDocument()
    expect(screen.getByText(/read-only access/i)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Browse projects' })).toHaveAttribute('href', '/projects')

    const features = screen.getByRole('list', { name: 'What you can do' })
    for (const title of ['Projects', 'Documents', 'Search', 'Ask']) {
      expect(features).toHaveTextContent(title)
    }

    expect(screen.queryByText(/403|forbidden|failed|error/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/upload/i)).not.toBeInTheDocument()
  })

  it('Technician landing page mentions uploading documents', () => {
    renderHome([Roles.Technician], 'Toby Technician')

    expect(screen.getByRole('heading', { name: 'Welcome, Toby Technician' })).toBeInTheDocument()
    expect(screen.getByText(/upload documents/i)).toBeInTheDocument()
    expect(screen.queryByText(/read-only access/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/403|forbidden|failed|error/i)).not.toBeInTheDocument()
  })

  it('user with no roles gets the welcome page and no analytics request', () => {
    renderHome([])

    expect(mockFetch).not.toHaveBeenCalled()
    expect(screen.getByRole('link', { name: 'Browse projects' })).toBeInTheDocument()
  })
})

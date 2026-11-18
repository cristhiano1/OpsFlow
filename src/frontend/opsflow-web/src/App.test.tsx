import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi } from 'vitest'
import { AuthContext } from './auth/authContext'
import type { AuthContextValue, AuthState, LoginResult } from './auth/authContext'
import { fetchAnalyticsSummary } from './analytics/analyticsApi'
import App from './App'

vi.mock('./projects/documentsApi', () => ({
  listDocuments: vi.fn().mockReturnValue(new Promise(() => {})),
  uploadDocument: vi.fn(),
  DocumentApiError: class extends Error {},
  ProjectNotFoundError: class extends Error {},
}))

vi.mock('./analytics/analyticsApi', () => ({
  fetchAnalyticsSummary: vi.fn().mockReturnValue(new Promise(() => {})),
}))

function makeContextValue(state: AuthState): AuthContextValue {
  return {
    state,
    login: vi.fn<(e: string, p: string) => Promise<LoginResult>>().mockResolvedValue({ kind: 'success' }),
    logout: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
    retryBootstrap: vi.fn(),
  }
}

function renderApp(
  state: AuthState,
  initialEntries: string[] = ['/'],
) {
  return render(
    <MemoryRouter initialEntries={initialEntries}>
      <AuthContext value={makeContextValue(state)}>
        <App />
      </AuthContext>
    </MemoryRouter>,
  )
}

const MOCK_USER = {
  userId: 'u1',
  email: 'alice@example.com',
  displayName: 'Alice',
  organizationId: 'org1',
  organizationName: 'Acme',
  roles: ['Organization Administrator'] as const,
}

const VIEWER_USER = {
  ...MOCK_USER,
  displayName: 'Vera Viewer',
  roles: ['Viewer'] as const,
}

describe('App routing', () => {
  it('shows loading screen during bootstrap', () => {
    renderApp({ status: 'loading' })
    expect(screen.getByText('Loading…')).toBeInTheDocument()
  })

  it('redirects unauthenticated user from / to /login', () => {
    renderApp({ status: 'unauthenticated' }, ['/'])
    expect(
      screen.getByText('Sign in to your account'),
    ).toBeInTheDocument()
  })

  it('shows authenticated shell at /', () => {
    renderApp({ status: 'authenticated', user: MOCK_USER }, ['/'])
    expect(screen.getByText('Alice')).toBeInTheDocument()
    expect(screen.getByText('Acme')).toBeInTheDocument()
  })

  it('redirects authenticated user from /login to /', () => {
    renderApp({ status: 'authenticated', user: MOCK_USER }, ['/login'])
    expect(screen.getByText('Alice')).toBeInTheDocument()
  })

  it('redirects unknown paths to /', () => {
    renderApp({ status: 'unauthenticated' }, ['/some/random/path'])
    expect(
      screen.getByText('Sign in to your account'),
    ).toBeInTheDocument()
  })

  it('renders DashboardPage inside AuthenticatedShell at /', () => {
    renderApp({ status: 'authenticated', user: MOCK_USER }, ['/'])
    expect(
      screen.getByRole('heading', { name: 'Dashboard' }),
    ).toBeInTheDocument()
    expect(screen.getByText('Alice')).toBeInTheDocument()
    expect(
      screen.getByRole('navigation', { name: 'Primary navigation' }),
    ).toBeInTheDocument()
  })

  it('lands a Viewer on the welcome page at / without requesting analytics', () => {
    vi.mocked(fetchAnalyticsSummary).mockClear()
    renderApp({ status: 'authenticated', user: VIEWER_USER }, ['/'])

    expect(
      screen.getByRole('heading', { name: 'Welcome, Vera Viewer' }),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Home' })).toHaveAttribute('href', '/')
    expect(screen.queryByRole('link', { name: 'Dashboard' })).not.toBeInTheDocument()
    expect(fetchAnalyticsSummary).not.toHaveBeenCalled()
  })

  it('redirects a Viewer from /login to the welcome page', () => {
    renderApp({ status: 'authenticated', user: VIEWER_USER }, ['/login'])
    expect(
      screen.getByRole('heading', { name: 'Welcome, Vera Viewer' }),
    ).toBeInTheDocument()
  })

  it('renders Projects link in sidebar at /', () => {
    renderApp({ status: 'authenticated', user: MOCK_USER }, ['/'])
    expect(screen.getByRole('link', { name: 'Projects' })).toHaveAttribute(
      'href',
      '/projects',
    )
  })

  it('renders ProjectWorkspacePage at /projects/:projectId inside AuthenticatedShell', () => {
    renderApp(
      { status: 'authenticated', user: MOCK_USER },
      ['/projects/proj-123'],
    )
    expect(
      screen.getByRole('tab', { name: 'Documents' }),
    ).toBeInTheDocument()
    expect(
      screen.getByRole('navigation', { name: 'Primary navigation' }),
    ).toBeInTheDocument()
  })
})

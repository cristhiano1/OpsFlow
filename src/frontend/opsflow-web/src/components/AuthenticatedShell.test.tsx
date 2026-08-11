import { render, screen } from '@testing-library/react'
import { MemoryRouter, Routes, Route } from 'react-router-dom'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi } from 'vitest'
import { AuthContext } from '../auth/authContext'
import type { AuthContextValue, LoginResult } from '../auth/authContext'
import { AuthenticatedShell } from './AuthenticatedShell'

function makeAuthValue(overrides?: Partial<AuthContextValue>): AuthContextValue {
  return {
    state: {
      status: 'authenticated',
      user: {
        userId: 'u1',
        email: 'alice@example.com',
        displayName: 'Alice',
        organizationId: 'org1',
        organizationName: 'Acme',
        roles: ['admin'],
      },
    },
    login: vi.fn<(e: string, p: string) => Promise<LoginResult>>().mockResolvedValue({ kind: 'success' }),
    logout: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
    retryBootstrap: vi.fn(),
    ...overrides,
  }
}

function renderShell(overrides?: Partial<AuthContextValue>) {
  const value = makeAuthValue(overrides)
  const result = render(
    <MemoryRouter initialEntries={['/']}>
      <AuthContext value={value}>
        <Routes>
          <Route path="/" element={<AuthenticatedShell />}>
            <Route index element={<p>child content</p>} />
          </Route>
        </Routes>
      </AuthContext>
    </MemoryRouter>,
  )
  return { ...result, auth: value }
}

describe('AuthenticatedShell', () => {
  it('shows user name and organization', () => {
    renderShell()
    expect(screen.getByText('Alice')).toBeInTheDocument()
    expect(screen.getByText('Acme')).toBeInTheDocument()
  })

  it('sign out button calls logout', async () => {
    const user = userEvent.setup()
    const { auth } = renderShell()

    await user.click(screen.getByRole('button', { name: 'Sign out' }))
    expect(auth.logout).toHaveBeenCalledTimes(1)
  })

  it('renders the Sidebar', () => {
    renderShell()
    expect(
      screen.getByRole('navigation', { name: 'Primary navigation' }),
    ).toBeInTheDocument()
  })

  it('renders child route content via Outlet', () => {
    renderShell()
    expect(screen.getByText('child content')).toBeInTheDocument()
  })
})

import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi } from 'vitest'
import { AuthContext } from '../auth/authContext'
import type { AuthContextValue, LoginResult } from '../auth/authContext'
import { AuthenticatedShell } from './AuthenticatedShell'

function renderShell(overrides?: Partial<AuthContextValue>) {
  const value: AuthContextValue = {
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
  const result = render(
    <AuthContext value={value}>
      <AuthenticatedShell />
    </AuthContext>,
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
})

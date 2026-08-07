import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi } from 'vitest'
import { AuthContext } from '../auth/authContext'
import type { AuthContextValue, LoginResult } from '../auth/authContext'
import { LoginPage } from './LoginPage'

function renderLoginPage(overrides?: Partial<AuthContextValue>) {
  const value: AuthContextValue = {
    state: { status: 'unauthenticated' },
    login: vi.fn<(e: string, p: string) => Promise<LoginResult>>().mockResolvedValue({ kind: 'success' }),
    logout: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
    retryBootstrap: vi.fn(),
    ...overrides,
  }
  const result = render(
    <AuthContext value={value}>
      <LoginPage />
    </AuthContext>,
  )
  return { ...result, auth: value }
}

describe('LoginPage', () => {
  it('renders email and password fields', () => {
    renderLoginPage()

    expect(screen.getByLabelText('Email')).toBeInTheDocument()
    expect(screen.getByLabelText('Password')).toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: 'Sign in' }),
    ).toBeInTheDocument()
  })

  it('calls login with entered credentials', async () => {
    const user = userEvent.setup()
    const { auth } = renderLoginPage()

    await user.type(screen.getByLabelText('Email'), 'alice@example.com')
    await user.type(screen.getByLabelText('Password'), 'secret123')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(auth.login).toHaveBeenCalledWith('alice@example.com', 'secret123')
  })

  it('shows error on invalid credentials', async () => {
    const user = userEvent.setup()
    const { auth } = renderLoginPage()

    vi.mocked(auth.login).mockResolvedValue({ kind: 'invalid-credentials' })

    await user.type(screen.getByLabelText('Email'), 'a@b.com')
    await user.type(screen.getByLabelText('Password'), 'wrong')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(
        'Invalid email or password',
      )
    })
  })

  it('shows service unavailable error without false wrong-password message', async () => {
    const user = userEvent.setup()
    const { auth } = renderLoginPage()

    vi.mocked(auth.login).mockResolvedValue({ kind: 'unavailable' })

    await user.type(screen.getByLabelText('Email'), 'a@b.com')
    await user.type(screen.getByLabelText('Password'), 'pw')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(
        'Service temporarily unavailable',
      )
    })
    expect(screen.queryByText(/invalid/i)).toBeNull()
  })

  it('disables form while submitting', async () => {
    const user = userEvent.setup()
    let resolveLogin!: (v: LoginResult) => void
    const loginFn = vi.fn<(e: string, p: string) => Promise<LoginResult>>().mockReturnValue(
      new Promise((resolve) => {
        resolveLogin = resolve
      }),
    )
    renderLoginPage({ login: loginFn })

    await user.type(screen.getByLabelText('Email'), 'a@b.com')
    await user.type(screen.getByLabelText('Password'), 'pw')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(screen.getByLabelText('Email')).toBeDisabled()
    expect(screen.getByLabelText('Password')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Signing in…' })).toBeDisabled()

    resolveLogin({ kind: 'invalid-credentials' })

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled()
    })
  })

  it('displays the logout notice when present', () => {
    renderLoginPage({
      state: {
        status: 'unauthenticated',
        notice: { message: 'Logout could not be confirmed.' },
      },
    })

    expect(screen.getByRole('status')).toHaveTextContent(
      'Logout could not be confirmed.',
    )
  })
})

import { useState } from 'react'
import type { FormEvent } from 'react'
import { useAuth } from '../auth/useAuth'
import './LoginPage.css'

export function LoginPage() {
  const { state, login } = useAuth()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)

    const result = await login(email, password)

    switch (result.kind) {
      case 'success':
      case 'replaced':
        break
      case 'invalid-credentials':
        setError('Invalid email or password.')
        setSubmitting(false)
        break
      case 'validation-failed':
        setError('Please check your input.')
        setSubmitting(false)
        break
      case 'unavailable':
        setError('Service temporarily unavailable. Please try again later.')
        setSubmitting(false)
        break
    }
  }

  const notice =
    state.status === 'unauthenticated' ? state.notice : undefined

  return (
    <div className="login-page">
      <div className="login-card">
        <h1 className="login-title">OpsFlow</h1>
        <p className="login-subtitle">Sign in to your account</p>
        {notice && (
          <div className="login-notice" role="status">
            {notice.message}
          </div>
        )}
        {error && (
          <div className="login-error" role="alert">
            {error}
          </div>
        )}
        <form onSubmit={handleSubmit}>
          <label className="login-label">
            Email
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
              disabled={submitting}
              autoComplete="email"
              className="login-input"
            />
          </label>
          <label className="login-label">
            Password
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              disabled={submitting}
              autoComplete="current-password"
              className="login-input"
            />
          </label>
          <button
            type="submit"
            disabled={submitting}
            className="login-button"
          >
            {submitting ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
      </div>
    </div>
  )
}

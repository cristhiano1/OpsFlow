import { Outlet } from 'react-router-dom'
import { useAuth } from '../auth/useAuth'
import { Sidebar } from './Sidebar'
import './AuthenticatedShell.css'

export function AuthenticatedShell() {
  const { state, logout } = useAuth()

  if (state.status !== 'authenticated') return null

  return (
    <div className="shell">
      <header className="shell-header">
        <div>
          <h1 className="shell-title">OpsFlow</h1>
          <p className="shell-org">{state.user.organizationName}</p>
        </div>
        <div className="shell-actions">
          <span className="shell-user">{state.user.displayName}</span>
          <button
            onClick={logout}
            type="button"
            className="shell-logout-button"
          >
            Sign out
          </button>
        </div>
      </header>
      <div className="shell-body">
        <Sidebar />
        <main className="shell-main">
          <Outlet />
        </main>
      </div>
    </div>
  )
}

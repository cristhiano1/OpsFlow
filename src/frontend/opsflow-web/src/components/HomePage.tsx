import { useAuth } from '../auth/useAuth'
import { canViewAnalytics } from '../auth/roles'
import { DashboardPage } from './DashboardPage'
import { WelcomePage } from './WelcomePage'

export function HomePage() {
  const { state } = useAuth()

  if (state.status !== 'authenticated') return null

  return canViewAnalytics(state.user.roles)
    ? <DashboardPage />
    : <WelcomePage user={state.user} />
}

import type { ReactNode } from 'react'
import { Navigate } from 'react-router-dom'
import { useAuth } from './useAuth'
import { LoadingScreen } from '../components/LoadingScreen'
import { UnavailableScreen } from '../components/UnavailableScreen'

export function ProtectedRoute({ children }: { children: ReactNode }) {
  const { state, retryBootstrap } = useAuth()

  switch (state.status) {
    case 'loading':
      return <LoadingScreen />
    case 'unauthenticated':
      return <Navigate to="/login" replace />
    case 'unavailable':
      return <UnavailableScreen onRetry={retryBootstrap} />
    case 'authenticated':
      return <>{children}</>
  }
}

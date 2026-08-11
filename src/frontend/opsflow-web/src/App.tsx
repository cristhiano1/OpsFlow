import { Routes, Route, Navigate } from 'react-router-dom'
import { useAuth } from './auth/useAuth'
import { ProtectedRoute } from './auth/ProtectedRoute'
import { LoginPage } from './components/LoginPage'
import { AuthenticatedShell } from './components/AuthenticatedShell'
import { LoadingScreen } from './components/LoadingScreen'
import { UnavailableScreen } from './components/UnavailableScreen'

function LoginRoute() {
  const { state, retryBootstrap } = useAuth()

  switch (state.status) {
    case 'loading':
      return <LoadingScreen />
    case 'authenticated':
      return <Navigate to="/" replace />
    case 'unavailable':
      return <UnavailableScreen onRetry={retryBootstrap} />
    case 'unauthenticated':
      return <LoginPage />
  }
}

function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginRoute />} />
      <Route
        path="/"
        element={
          <ProtectedRoute>
            <AuthenticatedShell />
          </ProtectedRoute>
        }
      />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

export default App

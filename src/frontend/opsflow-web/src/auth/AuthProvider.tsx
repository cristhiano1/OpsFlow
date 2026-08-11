import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react'
import type { ReactNode } from 'react'
import { login as authLogin, logout as authLogout } from './authApi'
import { AuthContext } from './authContext'
import type { AuthContextValue, AuthState, LoginResult } from './authContext'
import {
  type ExpectedEpoch,
  readEpoch,
  SESSION_EPOCH_STORAGE_KEY,
} from './sessionEpoch'
import {
  currentGeneration,
  invalidateSession,
  setSession,
  subscribeSessionInvalidation,
} from './sessionStore'
import { refreshOnce } from './singleFlightRefresh'

const MAX_BOOTSTRAP_REPLACED_RETRIES = 1

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AuthState>({ status: 'loading' })
  const stateRef = useRef<AuthState>(state)
  stateRef.current = state
  const bootstrapAttempted = useRef(false)
  const bootstrapSeqRef = useRef(0)
  const logoutInProgressRef = useRef(false)

  const runBootstrap = useCallback(() => {
    if (logoutInProgressRef.current) return
    const version = ++bootstrapSeqRef.current
    setState({ status: 'loading' })

    async function attemptBootstrap(replacedRetries: number): Promise<void> {
      if (bootstrapSeqRef.current !== version) return

      const epochRead = readEpoch()
      if (epochRead.status === 'unavailable') {
        if (bootstrapSeqRef.current === version) {
          setState({ status: 'unavailable' })
        }
        return
      }

      const expected: ExpectedEpoch =
        epochRead.status === 'present'
          ? { kind: 'present', epoch: epochRead.epoch }
          : { kind: 'missing' }

      const result = await refreshOnce(expected, null)

      if (bootstrapSeqRef.current !== version) return

      switch (result.kind) {
        case 'refreshed':
          setState({ status: 'authenticated', user: result.data.user })
          return
        case 'unauthenticated':
        case 'stale':
          setState({ status: 'unauthenticated' })
          return
        case 'session-replaced':
          if (replacedRetries < MAX_BOOTSTRAP_REPLACED_RETRIES) {
            return attemptBootstrap(replacedRetries + 1)
          }
          setState({ status: 'unavailable' })
          return
        case 'unavailable':
          setState({ status: 'unavailable' })
          return
      }
    }

    attemptBootstrap(0)
  }, [])

  const handleLogin = useCallback(
    async (email: string, password: string): Promise<LoginResult> => {
      const capturedGen = currentGeneration()
      const outcome = await authLogin({ email, password })

      if (outcome.kind === 'invalid-credentials')
        return { kind: 'invalid-credentials' }
      if (outcome.kind === 'validation-failed')
        return { kind: 'validation-failed' }
      if (outcome.kind === 'unavailable') return { kind: 'unavailable' }

      // Pre-install epoch check
      const preRead = readEpoch()
      if (preRead.status === 'unavailable') {
        setState({ status: 'unavailable' })
        return { kind: 'unavailable' }
      }
      if (preRead.status !== 'present' || preRead.epoch !== outcome.epoch) {
        runBootstrap()
        return { kind: 'replaced' }
      }

      // Install session
      const applied = setSession(
        outcome.data.accessToken,
        {
          userId: outcome.data.user.userId,
          organizationId: outcome.data.user.organizationId,
        },
        outcome.epoch,
        capturedGen,
      )
      if (!applied) {
        runBootstrap()
        return { kind: 'replaced' }
      }

      // Post-install epoch check
      const postRead = readEpoch()
      if (postRead.status === 'unavailable') {
        invalidateSession()
        setState({ status: 'unavailable' })
        return { kind: 'unavailable' }
      }
      if (postRead.status !== 'present' || postRead.epoch !== outcome.epoch) {
        invalidateSession()
        runBootstrap()
        return { kind: 'replaced' }
      }

      setState({ status: 'authenticated', user: outcome.data.user })
      return { kind: 'success' }
    },
    [runBootstrap],
  )

  const handleLogout = useCallback(async (): Promise<void> => {
    logoutInProgressRef.current = true
    ++bootstrapSeqRef.current

    try {
      let result: Awaited<ReturnType<typeof authLogout>>

      try {
        result = await authLogout()
      } catch {
        result = { kind: 'unavailable', error: undefined }
      }

      invalidateSession()

      if (result.kind === 'success') {
        setState({ status: 'unauthenticated' })
      } else {
        setState({
          status: 'unauthenticated',
          notice: {
            message:
              'Logout could not be confirmed. The server session may still be active.',
          },
        })
      }
    } finally {
      logoutInProgressRef.current = false
    }
  }, [])

  const retryBootstrap = useCallback(() => {
    if (logoutInProgressRef.current) return
    runBootstrap()
  }, [runBootstrap])

  useEffect(() => {
    if (bootstrapAttempted.current) return
    bootstrapAttempted.current = true
    runBootstrap()
  }, [runBootstrap])

  useEffect(() => {
    function handleStorageChange(event: StorageEvent) {
      if (event.key !== SESSION_EPOCH_STORAGE_KEY) return
      invalidateSession()
      if (!logoutInProgressRef.current) {
        runBootstrap()
      }
    }
    window.addEventListener('storage', handleStorageChange)
    return () => window.removeEventListener('storage', handleStorageChange)
  }, [runBootstrap])

  useEffect(() => {
    return subscribeSessionInvalidation(() => {
      if (stateRef.current.status === 'authenticated') {
        setState({ status: 'unauthenticated' })
      }
    })
  }, [])

  const value = useMemo<AuthContextValue>(
    () => ({
      state,
      login: handleLogin,
      logout: handleLogout,
      retryBootstrap,
    }),
    [state, handleLogin, handleLogout, retryBootstrap],
  )

  return <AuthContext value={value}>{children}</AuthContext>
}

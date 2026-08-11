import { createContext } from 'react'
import type { LoginUserResponse } from './contracts'

export interface AuthNotice {
  message: string
}

export type AuthState =
  | { status: 'loading' }
  | { status: 'authenticated'; user: LoginUserResponse }
  | { status: 'unauthenticated'; notice?: AuthNotice }
  | { status: 'unavailable' }

export type LoginResult =
  | { kind: 'success' }
  | { kind: 'invalid-credentials' }
  | { kind: 'validation-failed' }
  | { kind: 'replaced' }
  | { kind: 'unavailable' }

export interface AuthContextValue {
  state: AuthState
  login: (email: string, password: string) => Promise<LoginResult>
  logout: () => Promise<void>
  retryBootstrap: () => void
}

export const AuthContext = createContext<AuthContextValue | null>(null)

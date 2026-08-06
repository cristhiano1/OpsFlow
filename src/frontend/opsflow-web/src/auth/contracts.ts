// TypeScript mirrors of the backend authentication contracts. Property casing
// matches the JSON wire format ASP.NET Core produces for `AddControllers`
// (camelCase). These types intentionally hold NO logic; consumers pass and
// receive plain data.

export interface LoginRequest {
  email: string
  password: string
}

export interface LoginUserResponse {
  userId: string
  email: string
  displayName: string
  organizationId: string
  organizationName: string
  roles: readonly string[]
}

export interface LoginResponse {
  accessToken: string
  // ISO-8601 timestamp string produced by System.Text.Json for DateTimeOffset.
  accessTokenExpiresAt: string
  user: LoginUserResponse
}

// RefreshResponse is shape-identical to LoginResponse on the wire. Kept as a
// distinct alias so future divergence (e.g. adding a rotation counter) is a
// non-breaking type change on either side.
export type RefreshResponse = LoginResponse

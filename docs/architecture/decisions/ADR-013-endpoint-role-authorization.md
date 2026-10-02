# ADR-013: Endpoint Role Authorization

**Status:** Accepted  
**Date:** 2026-10-02

## Context

OpsFlow defines four organizational roles — Organization Administrator,
Coordinator, Technician, and Viewer — that are seeded, assigned to users, and
carried in JWT access tokens as `role` claims. However, all protected API
endpoints use a bare `[Authorize]` attribute that requires only authentication,
with no role-based restrictions. Every authenticated user has identical access
regardless of their role.

## Decision

We enforce role-based access control (RBAC) at the API boundary using ASP.NET
Core policy-based authorization. The design is intentionally minimal:

### Authorization policies

Two named policies are registered centrally alongside JWT authentication:

| Policy | Allowed Roles | Purpose |
|---|---|---|
| `ProjectManage` | Organization Administrator, Coordinator | Create or modify projects |
| `DocumentContribute` | Organization Administrator, Coordinator, Technician | Upload documents, trigger extraction |

Endpoints that all authenticated users may access (reads, search, answer, `/me`)
retain bare `[Authorize]`, which requires a valid JWT but no specific role.

### Role matrix

| Endpoint | Method | Admin | Coordinator | Technician | Viewer |
|---|---|---|---|---|---|
| Login / Refresh / Logout | POST | Anonymous | Anonymous | Anonymous | Anonymous |
| Me | GET | Y | Y | Y | Y |
| List Projects | GET | Y | Y | Y | Y |
| Create Project | POST | Y | Y | — | — |
| List Documents | GET | Y | Y | Y | Y |
| Upload Document | POST | Y | Y | Y | — |
| Get Content | GET | Y | Y | Y | Y |
| Extract Text | POST | Y | Y | Y | — |
| Get Extraction | GET | Y | Y | Y | Y |
| Search | POST | Y | Y | Y | Y |
| Answer | POST | Y | Y | Y | Y |

### 401 vs 403 behavior

- **Unauthenticated** → `401 Unauthorized` (no token or invalid token).
- **Authenticated, insufficient role** → `403 Forbidden`.
- Authorization never masks as 404, 400, or 500.

### Relationship to tenant isolation

RBAC and tenant isolation are independent, layered controls:

- **RBAC** (API boundary): checks whether the caller's role permits the
  operation.
- **Tenant isolation** (application layer): checks whether the caller's
  `org_id` claim owns the targeted resource.

A user with a valid role in Organization A still cannot access Organization B
data. Neither layer weakens the other.

### JWT role claims

Roles are emitted as `"role"` claims in the JWT payload. The JWT bearer
middleware is configured with `MapInboundClaims = false` and
`RoleClaimType = "role"`, so `RequireRole(...)` works directly with the
short-name claims without URI remapping.

## Alternatives considered

- **Attribute-based access control (ABAC):** Rejected as premature — the
  current four-role hierarchy with two coarse permission levels does not
  warrant a policy engine or permission database.
- **Scattering `[Authorize(Roles = "...")]` with literal strings:** Rejected in
  favor of centralized named policies and role constants to prevent typos and
  make intent explicit.
- **Per-action policies for every endpoint:** Rejected as excessive granularity.
  The two policies (`ProjectManage`, `DocumentContribute`) capture the only
  meaningful permission boundaries; read endpoints are open to all
  authenticated users.

## Consequences

- Viewer role is now genuinely read-only — cannot create projects, upload
  documents, or trigger extraction.
- Technician role can read and contribute documents but cannot manage projects.
- Future authorization changes only require updating the centralized policy
  registration and the corresponding controller attributes.
- Frontend should check roles from the login/me response to hide unavailable
  actions, but this is a UX convenience — enforcement is server-side.

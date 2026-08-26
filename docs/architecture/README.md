# Architecture Overview

This document describes the **current** OpsFlow foundation (Phase 0). It
deliberately describes only what exists today. Planned capabilities are noted as
planned, not as implemented.

## Style: modular monolith

OpsFlow is being built as a **modular monolith**: a single deployable backend
organized into clear internal layers, rather than a set of distributed
microservices. See
[ADR-001](decisions/ADR-001-modular-monolith.md) for the rationale.

## Backend projects and dependency direction

The backend solution (`OpsFlow.sln`) contains five projects under
`src/backend/`:

| Project                  | Role                                             | References |
|--------------------------|--------------------------------------------------|------------|
| `OpsFlow.Domain`         | Domain model (currently only an assembly marker) | none |
| `OpsFlow.Contracts`      | API request/response contracts (currently empty) | none |
| `OpsFlow.Application`    | Use cases / application logic (currently empty)  | `Domain` |
| `OpsFlow.Infrastructure` | Adapters (currently empty)                       | `Application`, `Domain` |
| `OpsFlow.Api`            | ASP.NET Core host (shell only)                   | `Application`, `Contracts`, `Infrastructure` |

The actual current project references are:

```mermaid
flowchart TD
    Api["OpsFlow.Api"]
    Application["OpsFlow.Application"]
    Infrastructure["OpsFlow.Infrastructure"]
    Contracts["OpsFlow.Contracts"]
    Domain["OpsFlow.Domain"]

    Application --> Domain
    Infrastructure --> Application
    Infrastructure --> Domain
    Api --> Application
    Api --> Contracts
    Api --> Infrastructure
```

- `Domain` and `Contracts` depend on no other OpsFlow project.
- `Application` depends on `Domain`.
- `Infrastructure` depends on `Application` and `Domain`.
- `Api` depends on `Application`, `Contracts` and `Infrastructure` and wires
  everything together.

At this stage these projects compile but contain no business logic. `Domain`
holds only a small `AssemblyMarker` type used by the backend smoke test.

## Frontend boundary

The frontend (`src/frontend/opsflow-web/`) is a React + TypeScript application
built with Vite. It is currently a minimal application shell and does not call
the backend API yet.

## Local SQL Server boundary

Local development uses Microsoft SQL Server 2025 (Developer edition) running in
Docker, defined in `docker-compose.yml`. See
[ADR-003](decisions/ADR-003-sql-server-2025-vector-foundation.md). EF Core
migrations and `OpsFlowDbContext` are configured, and the API uses the database
for implemented persistence-backed features (authentication, projects,
documents).

## Diagrams

- [System context](diagrams/system-context.md)

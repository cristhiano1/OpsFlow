# ADR-001: Start with a modular monolith

- **Status:** Accepted
- **Date:** 2026-07-29

## Context

OpsFlow is a portfolio project that must demonstrate solid architecture while
remaining realistic to build and run locally with free tools. The domain
(customers, work orders, assignments, comments, time tracking, attachments,
dashboards, audit, notifications) is cohesive and belongs to a single product.
There is no independent scaling requirement, no separate team ownership of
sub-systems, and no need for independent deployment of parts of the system at
this stage.

## Decision

Build OpsFlow as a **modular monolith**: a single deployable backend, internally
separated into clear layers — `Domain`, `Application`, `Contracts`,
`Infrastructure` and `Api` — with a dependency direction that points inward
toward the domain. Business functionality will later be organized as vertical
feature slices inside the `Application` layer.

Note: as of Phase 0 these projects exist and compile, but no domain modules or
business logic have been implemented yet.

## Consequences

**Positive**

- Simple to build, run and debug locally (a single process, a single database).
- Clear internal boundaries preserve most of the design benefits usually
  attributed to services, without distributed-system overhead.
- Easy to test end to end.
- Can be decomposed later if a genuine need appears, because the layering and
  module boundaries are explicit.

**Negative / trade-offs**

- All modules are deployed together; a change forces a redeploy of the whole
  backend.
- Module isolation is enforced by discipline and project references rather than
  by process boundaries.

## Why not microservices

Microservices would add distributed-system complexity — inter-service
communication, independent deployments, network failure modes, data consistency
across services, and more infrastructure — none of which is justified for a
single cohesive product built and run by one person on a local machine. Starting
with a well-structured modular monolith keeps the focus on domain design and
correctness, and leaves the door open to extract services later if a real need
emerges.

# ADR-004: Fixed embedding profile for v1

- **Status:** Accepted
- **Date:** 2026-08-27
- **Context from:** [ADR-003](ADR-003-sql-server-2025-vector-foundation.md)

## Context

OpsFlow needs document embeddings for future semantic search and retrieval.
The embedding pipeline must choose a model, a vector dimension, and a naming
convention for the profile that links a document's embedding set to the
generator that produced it.

At this stage the product has a single use case (semantic search over document
chunks) and no user-facing profile selection. Designing a dynamic
profile-registry system would add complexity without a concrete second
consumer.

SQL Server 2025 native `VECTOR(N)` supports float32 vectors with a maximum of
1998 dimensions. The chosen model must produce vectors within that limit.

## Decision

Ship a single hard-coded embedding profile for v1:

| Field        | Value                    |
|--------------|--------------------------|
| ProfileId    | `opsflow-semantic-v1`    |
| Dimensions   | `1536`                   |

The profile is defined as compile-time constants in
`EmbeddingProfiles.SemanticV1Id` and `EmbeddingProfiles.SemanticV1Dimensions`.

The `ModelId` is supplied by the `IEmbeddingGenerator` implementation at
runtime and recorded in `DocumentEmbeddingSet.ModelId`. This allows swapping
the concrete model without changing the profile identity, as long as the
output dimensions remain 1536.

The `EnsureDocumentEmbeddingsService` validates at the start of every
invocation that the injected generator's `Identity.ProfileId` and
`Identity.Dimensions` match the fixed profile. A mismatch is a configuration
error and throws `InvalidOperationException`.

The database enforces `UNIQUE(DocumentId, ProfileId)` on
`DocumentEmbeddingSets`, so each document can have at most one embedding set
per profile.

## Consequences

- **Simple:** No profile registry, no profile CRUD, no profile migration
  logic. The profile is a pair of constants.
- **Safe:** The 1536-dimension choice is well within the SQL Server 2025
  float32 limit of 1998 dimensions.
- **Extensible later:** Adding a second profile (e.g., a different model or
  dimension) requires adding new constants, updating the service validation,
  and the unique constraint already supports multiple profiles per document.
- **Constraint:** Changing the v1 profile's dimensions would require a data
  migration of all existing embedding sets. This is intentional — dimension
  changes are breaking changes that should be modeled as new profiles.

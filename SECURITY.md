# Security Policy

## Supported versions

OpsFlow has not reached a 1.0 release. There are no released, supported versions
yet. Until the first public release, any security fixes target the `main`
branch.

## Secrets and credentials

- **Never commit secrets, credentials or a real `.env` file.** The repository's
  `.gitignore` excludes `.env` and related files; only `.env.example` is
  committed.
- **`.env.example` contains development placeholders only.** The values in it
  (including the local SQL Server SA password) are non-secret defaults intended
  solely to make local development work out of the box.
- **Local SQL Server credentials must not be reused outside local development.**
  The development SA password is for a throwaway local container only. Never use
  it for any shared, staging or production system.

## Reporting a vulnerability

Private vulnerability-reporting instructions (a contact channel or a security
policy) will be added before the first public release. No security contact
address has been established yet, so none is listed here to avoid pointing to an
address that does not exist.

Until then, if you are collaborating on this project directly, raise security
concerns privately with the repository owner rather than in a public issue.

## Current scope

Phase 0 contains no authentication, authorization, tenant isolation, file
uploads or external integrations. Those security-relevant features — and the
threat model that accompanies them — will be documented as they are implemented
in later phases.

# ADR-002: Use SQL Server 2022 Developer in Docker for local development

- **Status:** Superseded by [ADR-003](ADR-003-sql-server-2025-vector-foundation.md) (2026-08-26)
- **Date:** 2026-07-29

## Context

OpsFlow needs a real relational database for local development that is free and
representative of what the application would use in production. The target
database engine is Microsoft SQL Server. It must run locally without any cloud
account and without interfering with other databases already present on the
developer's machine.

## Decision

Use **Microsoft SQL Server 2022, Developer edition**, running in Docker via
`docker-compose.yml`, for **local development only**.

Key configuration:

- **Image:** `mcr.microsoft.com/mssql/server:2022-latest` with
  `ACCEPT_EULA=Y` and `MSSQL_PID=Developer`.
- **Container port:** `1433` (SQL Server's default, inside the container).
- **Host port:** `14330`, published as `14330 -> 1433`.
- **Named persistent volume:** `opsflow-sql-data` mounted at
  `/var/opt/mssql`, so data survives container recreation.
- **SA password:** read from the environment (`MSSQL_SA_PASSWORD`) via
  interpolation; never hardcoded in the Compose file.

### Why host port 14330 instead of 1433

The default host port `1433` was already in use on the development machine by an
unrelated local project's SQL Server container. Publishing OpsFlow on host port
`14330` lets both run at the same time without a port conflict. Only the host
port differs; the container still listens on `1433` internally, and the
connection string in `.env.example` targets `localhost,14330`.

### Health check via SQLCMDPASSWORD

The container health check runs `sqlcmd` (from `/opt/mssql-tools18/bin`) with the
password supplied through the `SQLCMDPASSWORD` environment variable rather than
the `-P` command-line flag, so the password does not appear in the container's
process list. The check uses `-C` to trust the server's self-signed development
certificate and runs `SELECT 1`.

## Consequences

**Positive**

- Real SQL Server locally, free, with no cloud dependency.
- Coexists with other local SQL Server instances thanks to the non-default host
  port.
- Data persists across restarts via the named volume.
- The password is kept out of process listings in the health check.

**Negative / trade-offs**

- The non-default host port (`14330`) must be remembered and kept consistent
  across `.env.example`, the connection string and documentation.
- Docker Desktop must be running to develop against the database.

## Scope

This decision covers **local development only**. As of Phase 0 there is no
Entity Framework Core configuration, no migrations and no API-to-database
integration — the container simply provides a ready SQL Server instance for use
in later phases.

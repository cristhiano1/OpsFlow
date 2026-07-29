# OpsFlow Web

The React and TypeScript frontend for the **OpsFlow** enterprise
work-management platform.

## Current status

- Phase 0 foundation only.
- Minimal application shell.
- No backend API integration yet.
- No authentication or business functionality yet.

## Stack

- React 19
- TypeScript
- Vite
- oxlint
- Vitest
- Testing Library

## Local commands

Run these from `src/frontend/opsflow-web`:

```bash
npm ci
npm run lint
npm test
npm run build
npm run dev
```

- `npm ci` — installs dependencies exactly as pinned in `package-lock.json`.
- `npm run lint` — runs oxlint.
- `npm test` — runs Vitest in non-watch mode.
- `npm run build` — runs the TypeScript project build and the Vite production
  build.
- `npm run dev` — starts the local Vite development server.

## Scope

API integration, authentication, protected routes and business screens belong
to later phases. They are not part of the Phase 0 foundation.

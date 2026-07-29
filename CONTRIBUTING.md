# Contributing to OpsFlow

This document describes the current, lightweight development workflow. It will
grow as the project does.

## Workflow

1. **Create a branch** off `main` for your change:

   ```bash
   git checkout -b feat/short-description
   ```

2. **Keep changes focused.** One logical change per branch and pull request.
   Avoid unrelated refactors or reformatting in the same change.

3. **Run the validation locally** before opening a pull request.

   Backend:

   ```bash
   dotnet restore OpsFlow.sln
   dotnet build OpsFlow.sln -c Release --no-restore
   dotnet test OpsFlow.sln -c Release --no-build
   ```

   Frontend (from `src/frontend/opsflow-web`):

   ```bash
   npm ci
   npm run lint
   npm test
   npm run build
   ```

4. **Never commit secrets or `.env`.** Only `.env.example` (placeholders) is
   committed. See [SECURITY.md](SECURITY.md).

5. **Open a pull request** targeting `main`. CI
   ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs the same
   backend and frontend validation.

## Commit messages

Use short, readable, imperative messages. A conventional prefix is encouraged
but not strictly enforced:

```
feat: add customer creation endpoint
fix: correct work-order status transition guard
docs: document local SQL Server setup
chore: update CI Node version
```

Keep the summary line concise and add a body only when it adds context.

## Branch naming

Use a type prefix and a short description, e.g. `feat/customer-crud`,
`fix/login-redirect`, `docs/architecture-overview`.

# ColoringApp

Foundation for a Polish voice-to-coloring application. Development runs locally with Angular, ASP.NET Core, and PostgreSQL in Docker Compose. The next step in the [roadmap](docs/roadmap.md) is voice-to-coloring generation for guests.

## Prerequisites

- .NET SDK **10.0.404** (root `global.json`; no prereleases or roll-forward).
- nvm and Node **26.13.1**, with npm **11.20.0**. Run `nvm install` if necessary, then `nvm use` from the repository root. Check `node --version` and `npm --version` in the terminal you use for this project. If npm differs, select it with `npm install --global npm@11.20.0`.
- Docker Engine / Docker Desktop running, with Compose v2 support (`docker info`, `docker compose version`). Linux amd64 is used locally and in CI. PostgreSQL supports multiple architectures; the selected Flyway image publishes linux/amd64, so other hosts need Docker's amd64 emulation.
- For browser tests, install Playwright Chromium after `npm ci` (see verification below).

Selected stable versions: Angular/CLI **22.2.6**, TypeScript **6.0.3**, RxJS **7.8.2**, EF Core **10.0.4**, Npgsql EF provider **10.0.3**, PostgreSQL **18.7**, Flyway **13.12.2**, Testcontainers **4.17.0**, xUnit **2.9.3**, Vitest **5.1.0**, and Playwright **1.65.1**. Exact dependency trees are recorded in `frontend/package-lock.json` and backend `packages.lock.json` files. Images are pinned in `compose.yaml` and the shared `PostgresSandbox` test infrastructure; keep those pins aligned.

## First startup

Run root commands from this directory unless a different working directory is shown.

1. Activate Node and copy the configuration template:

   ```sh
   nvm use
   cp .env.example .env
   ```

   Edit ignored `.env`: choose a local database password, keeping `POSTGRES_DB` and `POSTGRES_USER` consistent. Optional `POSTGRES_PORT` changes the host binding if 5432 is occupied. Database exposure is restricted to loopback. Keep the file private; never commit it. Values with special characters should use Compose-compatible `.env` quoting (single quotes preserve literal `$` characters).

2. Copy those settings into .NET User Secrets without printing credentials or passing them as process arguments:

   ```sh
   node scripts/configure-local-secrets.mjs
   ```

   The helper reads `.env` and configures `ConnectionStrings:DefaultConnection` for the API's `UserSecretsId`. It connects to `localhost` and the mapped port. Flyway independently uses the same `.env` credentials and `postgres:5432` inside Docker. No passwords are committed or included in JDBC URLs. CI uses disposable credentials.

3. Start PostgreSQL and explicitly apply and validate migrations:

   ```sh
   docker compose up -d postgres
   docker compose run --rm flyway migrate
   docker compose run --rm flyway validate
   ```

   Only PostgreSQL is persistent. Flyway is a disposable tools-profile service and never runs inside the API. V1 creates `coloring_app`; history lives in `public.flyway_schema_history`. Repeating migration is safe. Flyway validates checksums and filenames; clean and automatic baselining are disabled.

4. Restore and start the API in one terminal:

   ```sh
   dotnet restore backend/ColoringApp.slnx --locked-mode
   dotnet run --project backend/ColoringApp.Api --launch-profile http
   ```

5. Restore and start Angular in another terminal:

   ```sh
   cd frontend
   npm ci
   npm start
   ```

Open **http://localhost:4200**. Angular proxies relative `/api/**` requests to **http://localhost:5213** without CORS configuration or development HTTPS redirection. The screen checks readiness once; failure offers a manual retry. There is no automatic polling.

## Subsequent startup and data preservation

Run `nvm use`, `docker compose up -d postgres`, `docker compose run --rm flyway migrate`, and `docker compose run --rm flyway validate`, then start the API and Angular as above. Repeat the secrets helper after changing connection settings. Changing `.env` credentials does not change an existing database user's password in a persisted volume.

Stop frontend/API with Ctrl+C. `docker compose stop postgres`, `docker compose start postgres`, and `docker compose down` preserve the named database volume. PostgreSQL 18 stores it under `/var/lib/postgresql`. Normal startup never resets data. Deleting the named volume is a separate destructive operation; it is intentionally excluded from setup instructions.

## API contracts

| Endpoint | Result |
| --- | --- |
| `GET /api/health` | `200 { "status": "ok", "application": "ColoringApp" }`, independent of PostgreSQL |
| `GET /api/health/ready` | `200 { "status": "ok", "application": "ColoringApp", "database": "ok" }` after querying schema existence |
| Readiness failure | `503 application/problem+json`, generic title and `code: "database_unavailable"` or `"database_not_initialized"` |

Both endpoints are anonymous, read-only, and send `Cache-Control: no-store`. The database check has a three-second deadline. Missing connection configuration fails startup with a non-secret diagnostic; temporary database failure leaves liveness available. Development OpenAPI is at `/openapi/v1.json`.

## Verification

Backend, from root (Docker must be available; tests fail rather than skip database checks):

```sh
dotnet restore backend/ColoringApp.slnx --locked-mode
dotnet build backend/ColoringApp.slnx --no-restore
dotnet test backend/ColoringApp.slnx --no-build
dotnet format backend/ColoringApp.slnx --verify-no-changes --no-restore
```

Frontend, from `frontend/`:

```sh
npm ci
npm run lint
npm run format:check
npm run build
npm run test:ci
npx playwright install chromium
npm run test:e2e
```

On Linux, use `npx playwright install --with-deps chromium` to install browser OS dependencies. Browser tests require the backend solution to have been built first; Playwright starts its own Kestrel API host on 5214, Angular on 4201, and disposable PostgreSQL/Flyway resources. Existing processes on those ports cause a failure instead of being reused. Normal shutdown disposes owned resources; Testcontainers' resource reaper handles abrupt exits. Tests never read `.env` or use the persistent local volume.

Integration checks cover safe health responses, missing schema, unavailable or stalled PostgreSQL, configuration failure, migration initialization, validation, no-op reruns, and checksum rejection on copied scripts. Unit tests use Angular's controlled HTTP backend for loading, success, failure, timeout, and retry. Playwright uses real API/database readiness plus a controlled initial failure and keyboard retry on desktop and at 375px width. This does not establish support for future microphone or print flows.

Apply formatting separately:

```sh
dotnet format backend/ColoringApp.slnx --no-restore
cd frontend
npm run format
```

`.github/workflows/checks.yml` runs these checks on pushes to `main` and pull requests with read-only permissions. A hosted run remains pending until an explicitly authorized push.

## Dependency and migration maintenance

Lockfiles are already supplied. For an intentional dependency change only, update package versions, run `dotnet restore backend/ColoringApp.slnx --force-evaluate` and/or `npm install` in `frontend/`, review regenerated lockfiles, and rerun checks. CI uses `--locked-mode` and `npm ci`. Do not use ordinary installation to silently replace reviewed locks.

Add schema changes as `backend/database/migrations/V<number>__<description>.sql` with increasing integers. Applied scripts are immutable. Never run EF migrations, `EnsureCreated`, automatic repair, clean, or baselining to bypass validation. Keep migrations compatible with the previous application release.

## Common failures

- **Docker cannot connect:** start Docker Desktop / the daemon and check the active context and socket access with `docker info`. Testcontainers supports `DOCKER_HOST` for nonstandard installations. Do not disable database tests to get a green build.
- **Occupied port:** adjust `.env`'s `POSTGRES_PORT`, rerun the secrets helper, and recreate only the PostgreSQL service with `docker compose up -d postgres`. Stop the conflicting process for 4200/5213; browser tests require 4201/5214.
- **Missing secrets:** run the helper and use the `http` development launch profile. The startup message names only the missing key. Do not print secrets while troubleshooting.
- **Database is running but readiness fails:** run Flyway migrate and validate. A missing `coloring_app` schema returns `database_not_initialized`; the API never creates it.
- **Flyway validation/checksum failure:** stop the migration workflow, restore the original applied script, and express new work in a new migration. Do not repair history, baseline, reset the volume, or ignore the failure.
- **Wrong Node/npm:** run `nvm use` in the actual terminal and check versions. `.npmrc` rejects incompatible engines.

Product, architecture, standards, and approved scope are in [docs](docs/), starting with [specification 001](docs/specs/001-project-foundation.md).

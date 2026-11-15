# 001 — Project Foundation

Status: Approved
Created: 2026-11-15
Approval: Approved by the user on 2026-11-15 for implementation by a subsequent agent. Commit and push require separate explicit authorization.
Roadmap stage: 1 — Local foundation and CI

Sources: [product](../product.md), [architecture](../architecture.md), [development guidelines](../development-guidelines.md), [roadmap](../roadmap.md), and [agent instructions](../../AGENTS.md).

## Goal and scope

Provide a reproducible local Angular → .NET → PostgreSQL application, with Flyway SQL migrations and meaningful automated checks. A subsequent agent must be able to implement and verify this slice without designing future product features.

Include:

- The existing .NET 10 API, a new Angular application, and a minimal responsive foundation screen.
- PostgreSQL and a one-off Flyway runner through Docker Compose.
- Database connectivity, initial schema migration, health endpoints, and sanitized failures.
- Version pins, development configuration, formatting, linting, tests, GitHub Actions checks, and startup documentation.

Exclude voice recording, AI calls, generation jobs, accounts, guest sessions, history, email, S3, Terraform resources, application container packaging, and AWS deployment. These belong to later roadmap stages. Do not introduce placeholder business tables or mock product flows.

## Inspected starting point

The repository is on `main`, with initial commit `243d81b` and a clean working tree before this documentation change. It contains `backend/ColoringApp.slnx` and `ColoringApp.Api`, targeting `net10.0`. The API has a weather-forecast example, demonstration middleware, and `GET /api/health`. No frontend, migration, or CI implementation exists yet.

Observed locally on 2026-11-15:

| Tool | Observation |
| --- | --- |
| .NET SDK | `10.0.404` is selected |
| Node.js | `26.13.1` is installed under nvm; the agent's default shell still selects `24.21.0` |
| npm with Node 26 | `11.20.0` |
| Docker CLI / Compose | `29.9.3` / `5.7.0` |
| Docker daemon | Not verified: the agent's environment denied access to the Docker socket |

The `.gitignore` currently excludes `package.json` and `package-lock.json`. Correct these rules during implementation so frontend manifests and lockfiles can be reviewed and committed. Keep generated files, dependency directories, local secrets, and test artifacts ignored.

## Approved tool baseline

| Component | Baseline and reproducibility |
| --- | --- |
| Backend | .NET 10; pin SDK `10.0.404` in `global.json`, disallow prereleases |
| Frontend tooling | Node `26.13.1` in root `.nvmrc`; npm `11.20.0` recorded in frontend configuration |
| Angular | Angular and CLI 22.x, TypeScript 6.0.x, RxJS 7.x; strict TypeScript, standalone components, no SSR |
| PostgreSQL | PostgreSQL 18, initially `postgres:18.7`; identical database version in local development and CI tests |
| Runtime data access | EF Core 10 and Npgsql EF provider 10; no EF migration tooling |
| Schema migration | Flyway Community using the official `redgate/flyway` image; pin an exact stable compatible tag before implementation is complete |
| Verification | xUnit, ASP.NET Core test host, Testcontainers for .NET, Vitest, Playwright Chromium, ESLint, Prettier, `dotnet format` |

Select compatible stable package patches and the exact Flyway image tag during implementation, record them in configuration and lockfiles, and report the choices. Do not use floating `latest` image tags or prerelease packages. Use `npm ci` and locked NuGet restoration in CI. Verify container support for the developer's architecture and the CI runner. An incompatible baseline requires a documented spec revision rather than a silent major-version change.

Angular's current compatibility table supports Node 26 with Angular 22 and TypeScript 6.0. Flyway documents PostgreSQL support and a bundled command-line PostgreSQL driver; a local Java installation is unnecessary when running its Docker image. See the references below.

## Local development and configuration

- Run Angular on `http://localhost:4200` and the retained API HTTP profile on `http://localhost:5213`.
- Use relative `/api/...` browser URLs and an Angular development proxy for `/api/**`. Avoid a separate permissive CORS policy. The development HTTP profile must not redirect these calls to an unconfigured HTTPS endpoint; online HTTPS requirements remain unchanged.
- Put root `compose.yaml` services named `postgres` and `flyway` in the repository. Start only PostgreSQL as a persistent development service. Run Flyway explicitly as a disposable tooling service.
- Bind PostgreSQL to loopback, default host port `5432`, with an override for port conflicts. Use a named volume and a database health check. Stopping or restarting the environment must preserve the volume; ordinary startup must never reset it. Use the volume location appropriate to the pinned PostgreSQL image.
- Keep Compose database credentials in ignored root `.env`, with `.env.example` documenting required variables. Use .NET User Secrets for `ConnectionStrings:DefaultConnection`; CI may inject its equivalent environment variable. Explain how to configure the same database name and credentials for both processes without logging them.
- Flyway connects to `postgres:5432` inside the Compose network; the local API uses the mapped host port. Pass Flyway credentials through environment configuration, not command arguments or committed URLs containing passwords.
- Missing required backend configuration must fail startup with a clear, non-secret diagnostic. A configured database being temporarily unreachable must not crash the API or prevent liveness checks.
- Automated tests use isolated disposable databases and credentials, never the developer's persistent volume. No AWS, OpenAI, or email credentials are needed for this stage.

## Database and Flyway contract

Store versioned SQL in `backend/database/migrations/`, using `V<number>__<description>.sql` and monotonically increasing integer versions. Keep shared non-secret Flyway configuration under `backend/database/` and mount migrations read-only in the runner.

The first migration, `V1__create_application_schema.sql`, creates the `coloring_app` schema. This reserves the application's namespace without prematurely defining user or generation entities. Explicitly keep Flyway's history table in `public.flyway_schema_history`; do not let Flyway auto-create `coloring_app` in place of executing V1. Configure the EF Core context's default schema as `coloring_app`; it needs no business entity sets in this slice.

Required behavior:

1. `migrate` on an empty database applies V1 and records successful execution in Flyway history.
2. A second `migrate` without new scripts succeeds without reapplying V1 or changing application schema objects.
3. `validate` after migration succeeds. Enable validation on migration and strict migration filename validation. An applied-script checksum mismatch must fail validation and stop the migration workflow.
4. Keep `clean` disabled and automatic baselining disabled. Do not automatically repair history or ignore validation failures. Applied SQL files are immutable; subsequent changes use new versions.
5. The API must never execute migrations or create its own schema, including during tests. No EF migrations, `Database.Migrate()`, `EnsureCreated()`, or PostgreSQL initialization scripts duplicating V1.
6. SQL files and the pinned Flyway runner are shared by local development, integration tests, and later deployment. The AWS runner and its network/identity configuration remain stage 2 work.

This stage proves schema persistence, not business-data persistence. Future specs introduce tables and update EF mappings with corresponding Flyway scripts. Backward-compatible schema changes and application rollback rules remain those in the architecture.

## API and user-visible behavior

Remove the weather-forecast example, its record type, demonstration middleware, and their obsolete HTTP-request examples. Preserve the existing liveness response and development OpenAPI support. Use framework logging rather than demonstration `Console.WriteLine` calls.

| Endpoint | Success | Failure |
| --- | --- | --- |
| `GET /api/health` | `200`, `{ "status": "ok", "application": "ColoringApp" }`; no database dependency | Ordinary host failure only |
| `GET /api/health/ready` | `200`, `{ "status": "ok", "application": "ColoringApp", "database": "ok" }` after a real database query confirms the `coloring_app` schema exists | `503` ProblemDetails with `code: "database_unavailable"` for connection/query failures, or `code: "database_not_initialized"` if the schema is absent |

Both endpoints are anonymous, read-only, and return `Cache-Control: no-store`. Bound the readiness database operation to three seconds using appropriate connection/query timeout and cancellation. Do not retry indefinitely. Return generic ProblemDetails titles and no connection strings, hostnames, SQL, credentials, or stack traces. Readiness verifies this slice's database prerequisite; it does not replace Flyway validation or future schema compatibility checks.

Angular renders a simple Polish-language page with the title **ColoringApp** and introductory text **„Twoje pomysły zamienimy w kolorowanki.”** On initial load it calls readiness through an HTTP service with explicit DTOs:

| State | UI behavior |
| --- | --- |
| Loading | Show „Sprawdzamy gotowość aplikacji…” |
| Ready | Show „Aplikacja jest gotowa.” |
| Failure | Show „Nie udało się połączyć z aplikacją. Spróbuj ponownie.” and a „Spróbuj ponownie” button |

Treat network failures, non-success responses, and a five-second frontend request timeout as the same visible failure state. Retry starts one new request, returns to loading, and prevents duplicate requests while pending. Do not poll automatically. Internal database codes and infrastructure details stay out of the UI.

Use semantic markup, a keyboard-operable retry button, visible focus, and accessible status announcements. The page must fit desktop and a 375-pixel-wide mobile viewport without horizontal overflow. This is a temporary foundation screen, not evidence that coloring-page generation is already available. No UI component framework is required for it.

## Verification and GitHub Actions

Create a checks workflow for every push to `main` and optional pull requests. Run on a Linux runner with Docker, the pinned SDK, and the pinned Node version. Use read-only repository permissions and no deployment secrets. Failed required commands must fail the workflow; do not suppress errors or silently skip database tests when Docker is unavailable.

Checks cover:

- Backend locked restore, build, formatting, and integration tests using `WebApplicationFactory` and real PostgreSQL through Testcontainers. The fixture runs the pinned Flyway image against its disposable database before ready-state tests; it must not emulate Flyway by executing SQL itself.
- Migration initialization, successful validation, no-op rerun, and checksum rejection. Test checksum rejection on a temporary copy of migration files and an isolated database, leaving repository migrations untouched. Share a fixture where appropriate to avoid repeated container startup.
- Liveness with unavailable PostgreSQL; readiness after migration, against an unmigrated database, and with unavailable PostgreSQL. Verify status, stable error code, and absence of internal details. Use bounded test deadlines with reasonable scheduling tolerance.
- Frontend linting, formatting, production build, and Vitest tests for loading, success, failure, timeout, and retry behavior using a controlled HTTP backend.
- A small Playwright Chromium suite: the foundation screen reaches ready through the real API and migrated PostgreSQL; a controlled failed readiness response followed by success exercises retry. Check desktop and mobile layouts. Browser tests must start and stop their own test processes and database resources.

CI can split independent checks into jobs. Keep commands aligned with local verification, upload useful failure reports without secrets, and clean up disposable resources even on failure. Do not add empty test projects, meaningless coverage targets, or empty Terraform jobs. Application-image publication and deployment are added in stage 2.

## Documentation and intended command interface

Add a root README with prerequisites, nvm activation, configuration, first startup, subsequent startup, verification, and common failures: Docker access, occupied ports, missing secrets, missing migrations, and failed validation. Document preservation of local data and keep destructive reset operations separate from normal setup.

The following is the intended interface to implement and verify, not a claim that these commands work in the current repository:

| Working directory | Command / purpose |
| --- | --- |
| Repository root | `nvm use` — activate the pinned Node version |
| Repository root | `docker compose up -d postgres` — start the local database |
| Repository root | `docker compose run --rm flyway migrate` — apply SQL migrations |
| Repository root | `docker compose run --rm flyway validate` — validate applied migrations |
| Repository root | `dotnet restore backend/ColoringApp.slnx --locked-mode` |
| Repository root | `dotnet run --project backend/ColoringApp.Api --launch-profile http` |
| Repository root | `dotnet build backend/ColoringApp.slnx --no-restore` |
| Repository root | `dotnet test backend/ColoringApp.slnx --no-build` |
| Repository root | `dotnet format backend/ColoringApp.slnx --verify-no-changes --no-restore` |
| `frontend/` | `npm ci`, then `npm start` — restore and start Angular |
| `frontend/` | `npm run build`, `npm run lint`, `npm run format:check`, `npm run test:ci` |
| `frontend/` | `npm run test:e2e` — orchestrated browser tests after documented browser setup |

Document initial lockfile creation, User Secrets configuration, `.env` setup, and Playwright browser installation before these verification commands. Supply formatter fix commands separately. Resolve any command incompatibility with selected stable tooling during implementation, update this spec and README together, and report the actual executed commands.

## Acceptance criteria

| ID | Expected result | Verification |
| --- | --- | --- |
| F-01 | A configured fresh checkout starts using the README and selected tool versions. | Follow the documented startup sequence; record actual versions. |
| F-02 | Angular reaches the retained API through `/api` and displays the ready state after migration. | Integration and real browser checks. |
| F-03 | Flyway alone creates `coloring_app`; V1 appears once in history; validation and a no-op rerun succeed. | Disposable PostgreSQL migration tests. |
| F-04 | Modifying an applied migration causes validation failure without automatic repair or schema reset. | Isolated checksum-failure test. |
| F-05 | Missing schema and unavailable database return the specified safe `503` responses; liveness still returns `200`. | Backend integration tests with bounded deadlines. |
| F-06 | Loading, failure, timeout, and successful retry work with Polish messages and no duplicate pending requests. | Vitest and the browser retry scenario. |
| F-07 | The screen is usable on desktop and mobile with keyboard focus and no horizontal overflow. | Browser inspection, including a 375-pixel viewport. |
| F-08 | Normal database stop/start preserves schema and Flyway history. Tests never use the local persistent database. | Local restart check and fixture/configuration review. |
| F-09 | Required builds, tests, lint, and format checks pass locally and in GitHub Actions. | Recorded local results and an observed hosted run after an authorized push. |
| F-10 | Manifests, lockfiles, and SQL are reviewable; secrets and generated artifacts are excluded. | Diff and ignore-rule review. |

Hosted CI cannot be verified before publication. If the user has not authorized a push, leave the implementation ready for review and explicitly report F-09 as locally verified / hosted verification pending. Do not commit, push, or mark the spec Implemented to bypass this boundary.

## Decisions and remaining prerequisites

- **Already approved:** Angular, .NET 10, PostgreSQL, GitHub Actions, Docker-based local database, and Flyway instead of EF schema migrations. EF Core remains the runtime ORM.
- **Approved in this specification:** the tool baseline, the dedicated `coloring_app` schema, endpoint contracts, foundation screen, test scope, and local command interface.
- **Implementation prerequisites:** activate installed Node 26; verify Docker daemon access and image compatibility; resolve and pin exact compatible package patches and the Flyway image. These are not evidence that the setup has already been tested.
- **Deferred to stage 2:** Flyway's AWS execution environment, deployment credentials and networking, RDS engine availability in the selected region, and the other deployment decisions in the architecture. Do not infer them from local Compose settings.
- **Model and approval policy:** use the advanced-model specification workflow in the development guidelines for substantive revisions. A suitable implementation agent may execute this approved spec; Git publication still requires separate explicit authorization.

## Short implementation plan

1. Confirm prerequisites, pin tooling, fix ignore rules, and establish package restoration and formatting configuration.
2. Add local PostgreSQL, the Flyway runner, V1, and migration verification against a disposable database.
3. Clean up the API template, configure EF Core/Npgsql, and implement health contracts and their failure tests.
4. Create the Angular application, proxy, foundation screen, and component/service tests.
5. Add browser verification and GitHub Actions checks; write and exercise the README instructions.
6. Review acceptance criteria and the diff, report results and limitations, and leave changes for user review without committing or pushing.

## Technical references

Consulted on 2026-11-15; verify exact package and container tags during implementation.

- [Angular version compatibility](https://angular.dev/reference/versions)
- [Angular development server and proxy](https://angular.dev/tools/cli/serve)
- [Flyway Docker and Community edition](https://documentation.red-gate.com/flyway/reference/usage/flyway-docker)
- [Flyway PostgreSQL support](https://documentation.red-gate.com/flyway/reference/database-driver-reference/postgresql-database)
- [Flyway validation](https://documentation.red-gate.com/flyway/reference/commands/validate)
- [PostgreSQL supported releases](https://www.postgresql.org/support/versioning/)
- [Npgsql EF Core 10](https://www.npgsql.org/efcore/release-notes/10.0.html)
- [Testcontainers PostgreSQL module](https://dotnet.testcontainers.org/modules/postgres/)

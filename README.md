# ColoringApp

ColoringApp turns a guest’s Polish voice recording into a printable coloring page. Angular, ASP.NET Core, PostgreSQL and private file storage run locally. The API uses OpenAI to transcribe, review and generate the result.

## Prerequisites

- .NET SDK **10.0.404** (root `global.json`; no prereleases or roll-forward).
- nvm and Node **26.13.1**, with npm **11.20.0**. Run `nvm install` if necessary, then `nvm use` from the repository root. Check `node --version` and `npm --version` in the terminal you use for this project. If npm differs, select it with `npm install --global npm@11.20.0`.
- Docker Engine / Docker Desktop running, with Compose v2 support (`docker info`, `docker compose version`). Linux amd64 is used locally and in CI. PostgreSQL supports multiple architectures; the selected Flyway image publishes linux/amd64, so other hosts need Docker's amd64 emulation.
- Desktop Chrome or Android Chrome for recording. Use localhost or HTTPS from a phone.
- For real generation, use an OpenAI project API key with access to the configured models. Automated tests supply a controlled provider.
- For browser tests, install Playwright Chromium after `npm ci` (see verification below).

Selected stable versions: Angular/CLI **22.2.6**, TypeScript **6.0.3**, RxJS **7.8.2**, EF Core **10.0.4**, Npgsql EF provider **10.0.3**, PostgreSQL **18.7**, Flyway **13.12.2**, OpenAI .NET SDK **2.15.0**, StbImageSharp **2.30.16** for PNG decoding, Testcontainers **4.17.0**, xUnit **2.9.3**, Vitest **5.1.0**, and Playwright **1.65.1**. Exact dependency trees are recorded in `frontend/package-lock.json` and backend `packages.lock.json` files. Images are pinned in `compose.yaml` and the shared `PostgresSandbox` test infrastructure; keep those pins aligned.

## First startup

Run root commands from this directory unless a different working directory is shown.

1. Activate Node and copy the configuration template:

   ```sh
   nvm use
   test -f .env || cp .env.example .env
   ```

   Edit ignored `.env`: choose a local database password, keeping `POSTGRES_DB` and `POSTGRES_USER` consistent. Optional `POSTGRES_PORT` changes the host binding if 5432 is occupied. Database exposure is restricted to loopback. Keep the file private; never commit it. Values with special characters should use Compose-compatible `.env` quoting (single quotes preserve literal `$` characters).

2. Copy those settings into .NET User Secrets without printing credentials or passing them as process arguments:

   ```sh
   node scripts/configure-local-secrets.mjs
   ```

   The helper reads `.env` and configures `ConnectionStrings:DefaultConnection` for the API's `UserSecretsId`. It connects to `localhost` and the mapped port. Flyway independently uses the same `.env` credentials and `postgres:5432` inside Docker. No passwords are committed or included in JDBC URLs. CI uses disposable credentials.

   To enable real generation, enter the OpenAI project API key into the API's .NET User Secrets. This command uses Python 3 to prompt without echoing the key or including it in process arguments.

   ```sh
   python3 -c 'import getpass, json; print(json.dumps({"OpenAI:ApiKey": getpass.getpass("OpenAI API key: ")}))' \
     | dotnet user-secrets set --project backend/ColoringApp.Api
   ```

3. Start PostgreSQL and explicitly apply and validate migrations:

   ```sh
   docker compose up -d postgres
   docker compose run --rm flyway migrate
   docker compose run --rm flyway validate
   ```

   Only PostgreSQL is persistent. Flyway is a disposable tools-profile service and never runs inside the API. V1 creates `coloring_app` and V2 adds guest sessions and generation jobs. V3 removes the obsolete temporary silence flag. Migration history lives in `public.flyway_schema_history`. Repeating migration is safe. Flyway validates checksums and filenames; clean and automatic baselining are disabled.

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

Open **http://localhost:4200**. Angular proxies relative `/api/**` requests to **http://localhost:5213** without CORS configuration or development HTTPS redirection. The screen checks readiness once. After submission, it polls the current generation every three seconds. A missing OpenAI key leaves readiness usable. A generation that needs the provider fails with a technical error.

## Subsequent startup and data preservation

Run `nvm use`, `docker compose up -d postgres`, `docker compose run --rm flyway migrate`, and `docker compose run --rm flyway validate`, then start the API and Angular as above. Repeat the secrets helper after changing connection settings. Changing `.env` credentials does not change an existing database user's password in a persisted volume.

Stop frontend/API with Ctrl+C. `docker compose stop postgres`, `docker compose start postgres`, and `docker compose down` preserve the named database volume. PostgreSQL 18 stores it under `/var/lib/postgresql`. Normal startup never resets data. The API stores recordings, staged images and approved images in separate folders under the operating system’s local application-data directory, in `ColoringApp`. Set `GenerationStorage__Root` to use another private, absolute directory outside the checkout and web root. Every API process connected to the same database must use the same private root. Keep the root across restarts alongside the database volume. Recordings are removed when jobs end and approved files are retained. The guest cookie expires after 24 hours.

## API contracts

| Endpoint | Result |
| --- | --- |
| `GET /api/health` | `200 { "status": "ok", "application": "ColoringApp" }`, independent of PostgreSQL |
| `GET /api/health/ready` | `200 { "status": "ok", "application": "ColoringApp", "database": "ok" }` after querying schema existence |
| Readiness failure | `503 application/problem+json`, generic title and `code: "database_unavailable"` or `"database_not_initialized"` |
| `POST /api/generations` | Takes one nonempty multipart `audio` file up to 5 MiB. The browser records WebM/Opus and stops at 60 seconds. The backend sends the original audio bytes to OpenAI for transcription. Returns `202`, a relative `Location` and an HttpOnly guest cookie on first acceptance. |
| `GET /api/generations/{id}` | Current job state for the owning guest, with `imageUrl` on success or `errorCode` on failure. |
| `GET /api/generations/{id}/image` | Approved PNG for the owning guest. Add `?download=true` for attachment. |

Both health endpoints are anonymous, read-only, and send `Cache-Control: no-store`. The database check has a three-second deadline. Missing connection configuration fails startup with a non-secret diagnostic; temporary database failure leaves liveness available. Development OpenAPI is at `/openapi/v1.json`. Guest reads use `Cache-Control: no-store` and return the same `404` for missing, expired and foreign results. Submission accepts the exact origin configured by `Frontend__Origin`, which defaults to `http://localhost:4200`.

## Verification

Run the backend checks from the repository root with Docker available.

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

On Linux, use `npx playwright install --with-deps chromium` to install browser OS dependencies. Browser tests require the backend solution to have been built first; Playwright starts its own Kestrel API host on 5214, Angular on 4201, and disposable PostgreSQL/Flyway resources. The test-only controls use loopback port 5215. Keep ports 4201 and 5214–5215 free. Normal shutdown disposes owned resources; Testcontainers' resource reaper handles abrupt exits. The fixtures use disposable database credentials and private temporary directories.

Integration checks cover health, V1-to-V3 migration, guest ownership and expiry, upload bounds and byte-for-byte forwarding, concurrent claims, worker restart, failed publication and cleanup. Playwright records synthetic audio with Chromium’s MediaRecorder, submits it to the real API, and checks the preview, download, one-page A4 print layout, keyboard use and mobile viewport. The browser host supplies an isolated database, private files and a controlled AI provider.

Apply formatting separately:

```sh
dotnet format backend/ColoringApp.slnx --no-restore
cd frontend
npm run format
```

`.github/workflows/checks.yml` runs these checks on pushes to `main` and pull requests with read-only permissions. A hosted run remains pending until an explicitly authorized push.

## Live integration smoke test

Use a non-personal sample spoken by an adult. In desktop Chrome on `http://localhost:4200`, record “Smok czyta książkę rycerzowi”, then stop. Check that the microphone is released and that exactly one approved preview appears. Check that the picture shows the requested subject and has black outlines, a white background and no shading. Download the PNG and confirm it is **1024 × 1536**. Open **Drukuj** and check the browser preview for one portrait A4 page with margins and just the picture. Record the elapsed time from stop to preview, the model IDs and the outcome from the safe API logs.

| Operation | Default model | Configuration override |
| --- | --- | --- |
| Polish file transcription | `gpt-transcribe` | `OpenAI__TranscriptionModel` |
| Assessment, enrichment and final prompt review | `gpt-6-luna` | `OpenAI__TextModel` |
| Text and image moderation | `omni-moderation-latest` | `OpenAI__ModerationModel` |
| Portrait PNG generation | `gpt-image-2.6-flare` | `OpenAI__ImageModel` |

Check access and options in the configured OpenAI project during the smoke test. Record any evaluated model substitution and its reason. Before sending actual children’s recordings, verify the project’s Zero Data Retention configuration and selected endpoint and model eligibility. [OpenAI under-18 guidance](https://developers.openai.com/api/docs/guides/safety-checks/under-18-api-guidance) · [OpenAI API data controls](https://developers.openai.com/api/docs/guides/your-data)

### Android Chrome over local HTTPS

Use a trusted local network. Put the development computer’s private LAN IP in `LAN_IP` and issue a local TLS certificate containing that IP as a SAN. One option is `mkcert`. Keep the certificate and private key outside this checkout and install only the local CA certificate on the test phone. Confirm Android Chrome trusts the origin without a certificate warning.

```sh
LAN_IP=192.168.1.10  # Replace with the development computer’s LAN address.
TLS_DIR="$HOME/.local/share/coloringapp-tls"
mkdir -p "$TLS_DIR"
mkcert -cert-file "$TLS_DIR/lan.pem" -key-file "$TLS_DIR/lan-key.pem" "$LAN_IP"
```

In the API terminal, add `Frontend__Origin="https://$LAN_IP:4200"` to the environment before restarting the normal localhost API. In the Angular terminal use the same `LAN_IP` and `TLS_DIR` and run the following from `frontend/`.

```sh
npm start -- --host "$LAN_IP" --ssl --ssl-cert "$TLS_DIR/lan.pem" --ssl-key "$TLS_DIR/lan-key.pem"
```

Open `https://<LAN_IP>:4200` from the phone. The Angular proxy forwards the HTTPS scheme to the loopback API, which marks the cookie Secure. Repeat the recording, download and print check on the physical phone. Return the API to its default frontend origin for desktop HTTP development.

## Dependency and migration maintenance

Lockfiles are already supplied. For an intentional dependency change only, update package versions, run `dotnet restore backend/ColoringApp.slnx --force-evaluate` and/or `npm install` in `frontend/`, review regenerated lockfiles, and rerun checks. CI uses `--locked-mode` and `npm ci`. Do not use ordinary installation to silently replace reviewed locks.

Add schema changes as `backend/database/migrations/V<number>__<description>.sql` with increasing integers. Applied scripts are immutable. Never run EF migrations, `EnsureCreated`, automatic repair, clean, or baselining to bypass validation. Keep migrations compatible with the previous application release.

## Common failures

- **Docker cannot connect:** start Docker Desktop / the daemon and check the active context and socket access with `docker info`. Testcontainers supports `DOCKER_HOST` for nonstandard installations. Do not disable database tests to get a green build.
- **Occupied port:** adjust `.env`'s `POSTGRES_PORT`, rerun the secrets helper, and recreate only the PostgreSQL service with `docker compose up -d postgres`. Stop the conflicting process for 4200/5213; browser tests require 4201 and 5214–5215.
- **Missing secrets:** run the database helper and use the `http` development launch profile. For a technical generation failure, check the OpenAI key and the stage/model in the API log.
- **Generation rejected:** in the API console, find the `Generation … stopped at …` line for the job ID from `/api/generations/{id}`. For `moderation_blocked`, look for the OpenAI moderation stage (`input`, `output`, or `unknown`), recognized public moderation categories, and the OpenAI request ID. OpenAI can omit these fields. An available request ID identifies the call for troubleshooting with OpenAI support.
- **Upload fails before a job is accepted:** check the configured frontend origin and the upload size. A recording that OpenAI cannot read is reported through the current job status.
- **Database is running but readiness fails:** run Flyway migrate and validate. A missing `coloring_app` schema returns `database_not_initialized`; the API never creates it.
- **Flyway validation/checksum failure:** stop the migration workflow, restore the original applied script, and express new work in a new migration. Do not repair history, baseline, reset the volume, or ignore the failure.
- **Wrong Node/npm:** run `nvm use` in the actual terminal and check versions. `.npmrc` rejects incompatible engines.

Product, architecture, standards, and approved scope are in [docs](docs/), including the [guest generation specification](docs/specs/002-guest-voice-to-coloring.md).

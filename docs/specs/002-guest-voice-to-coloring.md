# 002 — Local guest voice-to-coloring flow

**Status** · Approved by the user on 2026-11-19. The removal of external media tools was approved on 2026-11-20.

**Goal** · A guest opens ColoringApp on localhost, describes a coloring page in Polish, stops recording, and can preview, download and print the resulting page. The app and its data run locally. The backend calls OpenAI over the internet for real generation.

**Baseline** · At `bb8f671`, the Angular home screen checks readiness through the API proxy. The .NET application exposes health endpoints, Flyway V1 creates the empty `coloring_app` schema, and the browser test host uses disposable PostgreSQL. Extend these entry points and keep their checks working. See [stage 2](../roadmap.md), [product acceptance criteria](../product.md), [architecture](../architecture.md) and [development guidelines](../development-guidelines.md).

**Scope boundary** · This slice covers a guest's current result. Accounts, email, claiming and the save-to-account button belong to stage 3. History belongs to stage 4. Cloud resources, deployment, quotas, typed input and PDF export are outside this slice.

## Guest experience

- Make recording the primary action on the home screen. Request microphone permission on “Nagraj pomysł”. Show “Zatrzymaj nagrywanie”, elapsed time and the 60-second maximum. Stopping manually or reaching the limit releases the microphone and submits exactly once. Leaving the recording screen releases all tracks. Prevent double taps from submitting twice.
- Show actionable Polish feedback for denied permission, unavailable microphone, unsupported format or insecure browser context. Target current desktop Chrome and Android Chrome for recording. Use feature detection. Other browsers that cannot record an accepted format get a clear message. Document a secure local origin for testing on a physical phone.
- During upload and processing, show progress in words. Poll the authorized status URL every three seconds without overlapping requests. Stop at a terminal state, loss of authorization or component disposal. A transient failed status read offers “Sprawdź ponownie” for the same job. Never resubmit audio automatically after an ambiguous upload response.
- Show the approved preview, “Pobierz PNG”, “Drukuj” and “Nagraj kolejny pomysł”. Print only the loaded image on one portrait A4 page with margins, preserving its proportions. Use keyboard-operable controls and announce state changes. Store the current ID in page memory. The guest cookie supports authorized access for 24 hours without maintaining a list of results.
- Map `unintelligible_audio` to “Nie udało się zrozumieć opisu. Nagraj go jeszcze raz.” Map `content_rejected` to “Spróbuj opisać inny pomysł na kolorowankę.” For `generation_failed`, explain that a technical issue interrupted generation. Expired or unavailable results have a separate message. Every terminal failure allows a fresh recording.

## Submission, access and data

| Request | Contract |
| --- | --- |
| `POST /api/generations` | One multipart field `audio`. Persist private audio and commit the pending job with its guest association before returning `202`, `{ id, status: "Pending" }` and a relative status URL in `Location`. Issue a guest cookie on acceptance when needed. |
| `GET /api/generations/{id}` | Return `200` with `id` and `status`. Add `imageUrl` on success or the stable `errorCode` on failure or rejection. |
| `GET /api/generations/{id}/image` | Stream the approved `image/png`. Support `?download=true` for attachment with a server-generated filename. Only available for an authorized, successful job. |

- Record WebM/Opus using browser capability detection. The API accepts one nonempty multipart `audio` file up to 5 MiB. Bound the upload and return safe `ProblemDetails` for an invalid multipart recording (400), excessive size (413), or an unsupported request media type (415). Measure recording time in Angular and delegate audio decoding to OpenAI. A custom client can send a longer recording within the byte limit.
- Use a random opaque guest token of at least 256 bits, store only its digest, and reuse the valid session across submissions. Set a fixed server-side expiry of 24 hours from creation, with matching `HttpOnly`, host-only, `SameSite=Strict` cookie. Use `Secure` on HTTPS and verify the documented localhost setup. Restrict mutating browser requests to the configured frontend origin, including the first anonymous submission. Set result and status responses to `Cache-Control: no-store`. Missing, foreign, expired and nonexistent results have the same 404 response. Recheck ownership and expiry for every status and image read.
- Add Flyway V2 and matching EF mappings for guest sessions and generations. V3 removes the obsolete temporary silence flag while preserving V2 history and existing jobs. A generation has a UUID, guest association, nullable future account ID, state, UTC timestamps, recording key, worker ownership and expiry, safe error code, and success fields (unmodified transcript, final prompt, Polish keywords, approved PNG key). Use constraints for legal values and indexes for queue/cleanup queries. Leave account tables to stage 3. Preserve V1 and existing data.
- Keep recordings, staged output and approved images in private, configurable directories outside the web root and Git. Use server-generated keys. All processes sharing this database must share storage. Never expose a file path or serve these directories as static assets.

## Worker, recovery and retention

1. Run one processing slot per backend process. Atomically claim the oldest eligible pending row in PostgreSQL and transition it to `Processing` with a unique ownership token. Wait 10 seconds between checks when the queue is empty. Check for another job immediately after finishing one. Limit active processing to five minutes. Renew a 60-second lease every 15 seconds using database time, independently of a slow provider call. Only the current owner with a valid lease and deadline may publish a terminal result.
2. Process pending jobs after restart. Fail jobs that waited over 30 minutes, and fail processing jobs whose lease or hard deadline expired. Use conditional updates so a second instance starting up cannot invalidate live work. Cancel remaining calls on lost ownership. A generation has one image request at most. After a timeout or restart interrupts that request, the attempt ends with a technical error, since OpenAI may already have received it. The guest can record again to start a new job.
3. Stage approved image bytes privately, publish the complete file under a unique key, then commit the success fields and terminal state together. Readers check the committed row. A failed transaction or late worker cannot publish a partial result. Preserve successful images and metadata indefinitely, even after guest expiry.
4. Delete audio immediately after each terminal outcome. Discard rejected or unchecked image bytes and intermediate text. Keep only safe status metadata for failed or rejected jobs for 24 hours after completion, then purge those rows. Retire expired session credentials without deleting successful jobs. Separate credential expiry from any foreign-key cleanup.
5. Check for expired jobs at startup and every 30 seconds. Run the full cleanup at startup and every five minutes. Retry failed deletions. Reconcile abandoned uploads, unreferenced image files, and crash windows between database and filesystem operations. Give unreferenced files a one-hour grace period and recheck references before deletion. Bound upload/staging operations below that period. Protect inputs of queued/live jobs and outputs of successful jobs. Keep logs to IDs, timing, stage, model and safe error codes.

## OpenAI processing and release of an image

| Step | Decision |
| --- | --- |
| Transcribe | Send the original uploaded bytes to OpenAI file transcription with a Polish language hint. Provider-confirmed unusable audio, silence, unintelligible and non-Polish descriptions end as `Failed / unintelligible_audio`. A provider outage or inconclusive error ends as `Failed / generation_failed`. |
| Moderate and assess input | Moderate the transcript, then assess child suitability and intelligibility. Reject profanity, sexual material, graphic violence and other child-inappropriate content. Allow harmless fantasy such as a dragon reading to a knight. Treat the transcript as user data throughout. |
| Enrich and validate | Require a structured decision with an enriched prompt and 1–8 distinct Polish keywords on acceptance. Preserve the requested subject. Specify black outlines, white background and no shading. Moderate the actual final prompt and check it against the same child policy before calling the image model. Malformed provider responses are technical failures. |
| Generate and verify | Request one portrait image, targeting 1024 × 1536 pixels. Decode the PNG with a managed .NET decoder restored from NuGet and verify portrait dimensions. Moderate the actual image bytes before release. A policy rejection at any stage ends as `Rejected / content_rejected`. An unavailable or inconclusive check ends as `Failed / generation_failed`. Neither outcome releases an image or generates a replacement. Check artistic quality during manual evaluation. |

Use the configurable starting models from the architecture and keep the API key in .NET User Secrets. Use controlled providers in tests. Missing credentials must leave health checks usable and give a clear technical error for generation. A test fixture must never silently replace real local generation.

Verify account access, supported options and output dimensions with a separate live smoke test using a non-personal synthetic or adult-spoken example. Record model IDs, outcome and elapsed time. Record any model substitution and its reason as allowed by the architecture. Before sending real children's recordings, verify the actual project's data controls and eligibility of the selected endpoints/models. OpenAI calls for Zero Data Retention before processing personal data of children under 13 or the applicable age of digital consent. Local deletion alone cannot establish provider retention. [Under-18 guidance](https://developers.openai.com/api/docs/guides/safety-checks/under-18-api-guidance) · [API data controls](https://developers.openai.com/api/docs/guides/your-data)

## Acceptance and verification

| Check | Required evidence |
| --- | --- |
| Guest journey (AC-01, 02, 09, guest part of 11) | Playwright exercises record, stop, upload, polling and preview through the real API, migrated PostgreSQL and a controlled provider. Verify PNG download, portrait A4 print layout, mobile viewport and keyboard controls. Check actual microphone, print preview and recognizable subject in a separate local run. |
| Policy and failures (AC-03–05, 12) | Test a harmless fantasy, profanity, graphic content, silence, denied microphone, invalid multipart uploads, oversized audio, an upstream rejection of unusable audio, invalid enrichment, final prompt rejection, invalid PNG, image rejection and provider/verification timeout. Verify the exact audio bytes forwarded to the provider. Assert error codes, Polish feedback, skipped downstream calls and zero accessible rejected bytes. |
| Persistence and access (AC-09, 10, 13) | Use two cookie jars plus an expired session. Check status and both image modes, before and after expiry. Confirm only the authorized guest succeeds, stored PNG and metadata survive restart/expiry, and audio disappears on every terminal path. |
| Recovery (AC-14) | Against PostgreSQL, race two claimers. Exercise restart with a pending and a live job, an expired lease, late completion, failed file deletion and file publication followed by database failure. Verify one image call at most, safe terminal states and cleanup that preserves successful files. |
| Foundation regression | Test V1 → V2 → V3 on an existing database and a fresh migrate/validate/rerun. Replace the foundation test's empty-schema assertion with checks for the new schema while retaining V1 checksum protection. Run the existing backend, frontend and CI-equivalent checks against isolated storage and PostgreSQL. |

## Approved decisions and implementation plan

The approved scope includes the browser and format target, recording bounds, worker deadlines, failure retention and portrait size above. Provider access, retention configuration and real-device results require empirical verification. Report any unavailable check separately and leave acceptance open for that check.

1. Add Flyway migrations, mappings, private storage, guest access and the submission/status/image contracts.
2. Implement the worker, provider sequence, ownership checks and cleanup. Prove the failure and restart cases with controlled providers.
3. Connect Angular recording, polling, preview, download and print to the API. Extend the existing Playwright host with a test-only provider and isolated files.
4. Verify acceptance and update local setup and test instructions, including a real-integration smoke test. Record outcomes and remaining limitations for user review.

# ColoringApp — Architecture

Status: Approved architecture baseline; open decisions are listed below
Approval date: 2026-11-14
Flyway migration decision approved: 2026-11-15
Local development baseline approved: 2026-11-19
Product requirements: [product.md](product.md)

This document records approved decisions for local development of the university MVP. Resolve open decisions before their dependent implementation work. See [development-guidelines.md](development-guidelines.md) for engineering conventions and feature specifications, [AGENTS.md](../AGENTS.md) for agent responsibilities, and [roadmap.md](roadmap.md) for implementation order.

## Application and local environment

| Concern | Approved choice |
| --- | --- |
| Frontend | Angular, responsive Polish-language UI |
| Backend | C# / ASP.NET Core Minimal API on .NET 10 |
| Database | PostgreSQL in Docker Compose with a persistent volume, using EF Core and Npgsql |
| Database migrations | Versioned SQL applied by the one-off Flyway Compose service |
| File storage | Private directories on the backend host for temporary recordings and approved images |
| AI provider | OpenAI API, called from the backend |
| Email | A backend delivery adapter with a local mailbox for development and a controlled substitute for automated tests |
| Continuous integration | GitHub Actions for builds, style checks, and automated tests |

Run Angular and ASP.NET Core as separate development processes. Angular proxies relative `/api` requests to the API. Keep one backend application organized by functionality.

### Backend responsibilities

| Area | Responsibility |
| --- | --- |
| `Authentication` | Accounts, email-code challenges, authenticated sessions, and guest sessions |
| `Generations` | Recording submission, persistent jobs, processing, status, results, and claiming guest results |
| `History` | Authorized access to successful generations owned by the current user |
| Integrations | Focused components for OpenAI, private file storage, and email delivery |

Endpoints validate requests, enforce access, and delegate work. A generation service owns the processing sequence and is called by an ASP.NET Core `BackgroundService` in the same application. External API credentials and calls remain on the backend.

## Generation lifecycle

### Submission and execution

1. The browser records speech using explicit start and stop controls, then uploads the completed recording to the API.
2. The backend writes the recording to private temporary storage and creates a `Pending` generation in PostgreSQL. Confirm acceptance only when both are available.
3. Return `202 Accepted` with a generation identifier. The identifier alone does not authorize access.
4. The `BackgroundService` atomically claims a pending generation and marks it `Processing`.
5. Execute the AI sequence below.
6. Store an approved PNG in private image storage and its metadata in PostgreSQL. Mark the generation `Succeeded` only when the approved result is available.
7. Delete the temporary recording after every terminal outcome. A cleanup mechanism retries unsuccessful deletions and removes orphaned recordings after failures.

PostgreSQL is the durable job source; an in-memory queue must not be the sole record of accepted work. Handle partial failures between file storage and PostgreSQL so that orphaned inputs or outputs do not become accessible results.

Job claiming prevents concurrent processing of the same generation across API processes. PostgreSQL enforces conditional ownership and lease expiry. [Specification 002](specs/002-guest-voice-to-coloring.md) defines lease renewal, queue expiry, and processing deadlines.

### Status model

| Status | Meaning |
| --- | --- |
| `Pending` | Accepted and waiting for processing |
| `Processing` | Claimed by a worker and being processed |
| `Succeeded` | Approved image and required metadata are available |
| `Rejected` | Content was rejected by application policy or moderation |
| `Failed` | Processing failed, including unusable audio, technical errors, or an interrupted attempt |

`Succeeded`, `Rejected`, and `Failed` are terminal. History includes only `Succeeded` generations.

After a restart, pending jobs can be processed using their persisted recordings. Interrupted in-progress jobs end with a technical failure rather than automatically restarting image generation. Do not mark a job as interrupted merely because another application instance starts; distinguish abandoned work from work still owned by a live worker.

An image-generation call may have completed remotely even if the application did not receive its result. Do not blindly repeat such calls. The MVP accepts that a process restart can fail an active attempt and leave the user needing to record again.

### AI sequence

1. Send the original recording bytes to OpenAI file transcription and preserve the raw transcript separately from later transformations. Angular stops recording at 60 seconds. The API bounds upload size at 5 MiB.
2. Moderate the transcript.
3. Assess whether the description is understandable and appropriate under the product's child-oriented rules, including profanity. For an accepted description, prepare the enriched prompt and keywords.
4. Validate the final prompt before image generation.
5. Generate one portrait coloring page.
6. Automatically check the image before making it available.

Prompt enrichment preserves the child's intended subject while specifying clear outlines, a white background, and no shading. General moderation is supplemented by application-policy assessment; it is not a complete profanity filter or a guarantee of suitability for children.

Rejected images are not exposed or added to successful history. End the attempt without automatically generating a replacement. If a required check fails technically, withhold the image and report a technical failure rather than treating it as an approved or policy-rejected result.

### Approved starting models

| Task | Model ID |
| --- | --- |
| File transcription | `gpt-transcribe` |
| Description assessment, enrichment, and keywords | `gpt-6-luna` |
| Text and image moderation | `omni-moderation-latest` |
| Image generation | `gpt-image-2.6-flare` |

Keep model IDs configurable. These are the approved starting choices, not evidence that account access or result quality has been tested. Verify availability and evaluate Polish children's speech and representative coloring-page descriptions during implementation. Changes based on that evaluation are permitted; record the reason and selected configuration.

Moderation supports specific categories and is not a general quality evaluator or a complete child-safety assessment. Use the provider's supported workflows and validate the application policy with permitted and rejected examples.

## Communication and errors

- Use HTTP APIs with JSON for application data and file upload for recordings.
- The frontend polls authorized generation status approximately every three seconds and stops after a terminal result.
- Request failures use `ProblemDetails` with a stable machine-readable error code, such as `invalid_code`.
- Accepted jobs expose processing failures in their status, with codes such as `unintelligible_audio`, `content_rejected`, and `generation_failed`.
- Angular maps codes to short, actionable Polish messages. Do not expose raw provider errors or internal exceptions.
- Distinguish inability to understand a recording, policy rejection, and technical failure.

Exact endpoint contracts and DTOs will be specified with their implementation slices. Polling and temporary guest access do not create a guest-history feature.

## Data model and retention

Flyway owns database schema migrations. Use the same versioned SQL scripts for local development and integration tests. EF Core mappings must match the SQL schema; do not use EF Core migrations, `Database.Migrate()`, or `EnsureCreated()` to manage it. Run Flyway explicitly before starting the API, as described in the [local setup](../README.md#first-startup).

The following are conceptual entities, not a final SQL schema:

| Entity | Required information |
| --- | --- |
| User | Account identifier, verified email, account creation metadata |
| Login challenge | Challenge identifier, email, protected code verifier, expiry, attempt count, resend and consumption state |
| Guest session | Secure session association and expiry, sufficient to authorize its generations |
| Generation | Identifier, status, dates, nullable `UserId`, guest-session association when applicable, temporary audio file key, transcript, final prompt, keywords, image file key, and safe failure metadata |

A generation record exists from submission onward and is progressively populated. Store approved images as files and keep their stable, server-generated keys in PostgreSQL. Keep the storage directories outside the web root and version control. Keep approved images across backend restarts. `UserId = null` identifies a generation not assigned to an account.

Retain successful account and guest generations indefinitely in the MVP, including their approved images, transcripts, prompts, and keywords. Do not introduce expiry or automatic deletion of successful guest generations. Expiry of a guest's access does not delete the generation.

Audio is temporary. Delete it after success, rejection, or failure; clean up after interrupted processing as well. Storage settings and cleanup must not inadvertently preserve an audio archive. This describes data under application control; provider-side retention must be reviewed separately.

Failed and rejected generations retain safe status metadata for 24 hours after completion. Cleanup retires expired guest credentials without deleting successful generations. [Specification 002](specs/002-guest-voice-to-coloring.md) defines file reconciliation and its one-hour grace period.

### Access to images and guest results

- The backend checks guest or account access before streaming an approved image for preview or download. Keep temporary recordings private.
- An opaque 256-bit guest token is carried by a host-only `HttpOnly`, `SameSite=Strict` cookie with `Secure` on HTTPS. Only its digest is stored. A session grants access for 24 hours from creation, including status, result retrieval, and future account claiming.
- The cookie carries an access identifier, not a local list of coloring pages. Do not persist guest history in the browser.
- After the guest session expires, its unclaimed generations remain stored but are no longer accessible or claimable through that session.
- Claiming a generation requires both valid access to the guest result and successful account authentication. Assign it atomically, without generating another image, and end access to that generation through the guest session.
- Account history and download access are restricted to the owning account.

## Authentication

The .NET backend manages accounts and one-time email-code challenges. A delivery adapter sends the messages to the configured mailbox. Create an account only after successful email verification, or sign into the existing account for that email.

| Setting | Approved value |
| --- | --- |
| Code validity | 10 minutes |
| Maximum verification attempts | 5 per challenge |
| Resend cooldown | 60 seconds |
| Resend behavior | Invalidate the preceding code |
| Code consumption | Atomic and single-use |
| Authenticated cookie lifetime | 7 days from login, without sliding renewal |
| Logout | Available before expiry |

Never store or log plaintext codes. Use ASP.NET Core cookie authentication with `HttpOnly`, `Secure`, appropriate `SameSite`, and CSRF protection for state-changing operations. Generation quotas remain outside the MVP; authentication protections are a separate requirement.

When implementing authentication, persist ASP.NET Core Data Protection keys in a private local directory with a stable application name. Keep the key directory separate from stored generation files and use isolated keys in tests. Verify that the local session survives a backend restart.

## Environments and verification

- **Development** uses the Angular and .NET development processes, PostgreSQL in Docker Compose, and the explicitly invoked Flyway runner.
- **Automated tests** use disposable PostgreSQL and Flyway containers with separate data and credentials. Use controlled substitutes for OpenAI and email.
- Keep the local Compose settings in ignored `.env` and backend credentials in .NET User Secrets.
- GitHub Actions runs locked dependency restores, builds, lint, formatting, and automated tests on pushes to `main` and pull requests.

## Observability and testing

Write application logs through ASP.NET Core logging. Record generation identifiers, processing stages, elapsed time, model IDs, and safe technical error codes. Exclude audio, transcripts, prompts, email addresses, login codes, credentials and access tokens. Provider diagnostics must not leak request content through exceptions or HTTP logs.

Use the existing API liveness and database readiness endpoints to verify local availability.

Test strategy:

- Unit tests for code expiry and consumption, guest access, claiming, policy decisions, and generation transitions.
- Integration tests using real PostgreSQL for persistence, account isolation, atomic worker claiming, and concurrent authentication or result-claim attempts.
- A small set of browser tests for guest generation, authentication and saving, history, and errors.
- Controlled substitutes for OpenAI and email delivery in automated tests.
- Separate checks of actual microphone use, Polish transcription, moderation, image quality, email delivery, and printing.

Approved test tools and CI expectations are defined in [development-guidelines.md](development-guidelines.md): xUnit, PostgreSQL through Testcontainers, Vitest, and Playwright with Chromium initially in CI. There is no mandatory coverage percentage.

## Accepted limitations

- No application generation quotas, including for guests; paid usage can grow without an application cap.
- Successful generations are retained indefinitely, including unclaimed guest results.
- Process failures can terminate active generations. Interrupted image-generation calls are not automatically repeated.
- External AI quality and moderation effectiveness require evaluation, and email login depends on delivery.

## Open decisions and prerequisites

| Area | Required decision or validation | Resolve before |
| --- | --- | --- |
| Authentication details | Code format and verifier protection, precise challenge throttling, and authenticated cookie settings | Authentication implementation |
| AI integration verification | Verify account access, supported options, output dimensions, and model quality for the workflow in specification 002 | Real AI acceptance testing |
| Provider data | OpenAI-side retention and applicable processing configuration, separately from deletion in application storage | Sending real users' recordings |
| Local email and session verification | Choose the local mailbox tool and verify session persistence across backend restarts | Authentication acceptance |
| Tooling and browser coverage | Exact compatible tool versions, executable CI commands, and supported browsers beyond initial Chromium CI coverage; follow [development-guidelines.md](development-guidelines.md) | Their dependent implementation slices |

Open entries are not permission to choose a materially different architecture silently. Resolve them through the remaining design stages and record the outcome before dependent work.

## Reference documentation

References were consulted during design; verify compatibility when implementing.

- [Npgsql EF Core 10](https://www.npgsql.org/efcore/release-notes/10.0.html)
- [Flyway Docker](https://documentation.red-gate.com/flyway/reference/usage/flyway-docker) and [PostgreSQL support](https://documentation.red-gate.com/flyway/reference/database-driver-reference/postgresql-database)
- [ASP.NET Core hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)
- [ASP.NET Core cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0) and [Data Protection](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0)
- [OpenAI transcription](https://developers.openai.com/api/docs/models/gpt-transcribe), [text model](https://developers.openai.com/api/docs/models/gpt-6-luna), [moderation](https://developers.openai.com/api/docs/guides/moderation), and [image model](https://developers.openai.com/api/docs/models/gpt-image-2.6-flare)

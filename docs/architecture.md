# ColoringApp — Architecture

Status: Approved architecture baseline; open decisions are listed below
Approval date: 2026-11-14
Flyway migration decision approved: 2026-11-15
Product requirements: [product.md](product.md)

This document records approved decisions for the university MVP. Open decisions must be resolved before their dependent implementation or deployment work. Engineering conventions and feature-specification workflow are defined in [development-guidelines.md](development-guidelines.md); agent responsibilities are defined in [AGENTS.md](../AGENTS.md), and implementation sequencing in [roadmap.md](roadmap.md).

## Application and infrastructure

| Concern | Approved choice |
| --- | --- |
| Frontend | Angular, responsive Polish-language UI |
| Backend | C# / ASP.NET Core Minimal API; retain the existing .NET 10 project |
| Database | PostgreSQL with EF Core and Npgsql |
| Database migrations | Flyway with versioned SQL scripts; EF Core remains the runtime ORM |
| Deployment unit | One Docker image containing the published backend and built Angular static files |
| Hosting | AWS Elastic Beanstalk, Docker platform |
| Database hosting | Amazon RDS for PostgreSQL |
| Object storage | Private Amazon S3 storage |
| Email delivery | Amazon SES |
| AI provider | OpenAI API |
| AWS Region | `eu-central-1` (Frankfurt) |
| Infrastructure as code | Terraform |
| CI/CD | GitHub Actions |
| Container registry | Amazon ECR |
| Runtime secrets | AWS Systems Manager Parameter Store, `SecureString` |
| Logs | Amazon CloudWatch Logs, seven-day retention |

ASP.NET Core serves both the frontend and `/api` under one origin. Local development can run Angular and .NET separately. Keep one backend application organized by functionality; separate projects for every layer are unnecessary for this MVP.

### Backend responsibilities

| Area | Responsibility |
| --- | --- |
| `Authentication` | Accounts, email-code challenges, authenticated sessions, and guest sessions |
| `Generations` | Recording submission, persistent jobs, processing, status, results, and claiming guest results |
| `History` | Authorized access to successful generations owned by the current user |
| Integrations | Focused components for OpenAI, S3, and SES |

Endpoints validate requests, enforce access, and delegate work. A generation service owns the processing sequence and is called by an ASP.NET Core `BackgroundService` in the same application. External API credentials and calls remain on the backend.

## Generation lifecycle

### Submission and execution

1. The browser records speech using explicit start and stop controls, then uploads the completed recording to the API.
2. The backend stores the recording temporarily in private S3 and creates a `Pending` generation in PostgreSQL. Confirm acceptance only when both are available.
3. Return `202 Accepted` with a generation identifier. The identifier alone does not authorize access.
4. The `BackgroundService` atomically claims a pending generation and marks it `Processing`.
5. Execute the AI sequence below.
6. Store an approved PNG in S3 and its metadata in PostgreSQL. Mark the generation `Succeeded` only when the approved result is available.
7. Delete the temporary recording after every terminal outcome. A cleanup mechanism retries unsuccessful deletions and removes orphaned recordings after failures.

PostgreSQL is the durable job source; an in-memory queue must not be the sole record of accepted work. Handle partial failures between S3 and PostgreSQL so that orphaned inputs or outputs do not become accessible results.

Job claiming must prevent concurrent processing of the same generation, including overlapping application processes during deployment. Ownership, stale-job detection, and processing deadlines remain implementation prerequisites; see open decisions.

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

An image-generation call may have completed remotely even if the application did not receive its result. Do not blindly repeat such calls. The MVP accepts that a restart or deployment can fail an active attempt and leave the user needing to record again.

### AI sequence

1. Transcribe the recording, preserving the raw transcript separately from later transformations.
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

Flyway owns database schema migrations. Keep versioned SQL scripts in the repository and use the same scripts locally, in integration tests, and during deployment. EF Core mappings must match the SQL schema; do not use EF Core migrations, `Database.Migrate()`, or `EnsureCreated()` to manage it. Run Flyway as a separate explicit step, not during API startup. The foundation spec defines the local setup; the AWS execution environment remains a deployment prerequisite.

The following are conceptual entities, not a final SQL schema:

| Entity | Required information |
| --- | --- |
| User | Account identifier, verified email, account creation metadata |
| Login challenge | Challenge identifier, email, protected code verifier, expiry, attempt count, resend and consumption state |
| Guest session | Secure session association and expiry, sufficient to authorize its generations |
| Generation | Identifier, status, dates, nullable `UserId`, guest-session association when applicable, temporary audio key, transcript, final prompt, keywords, image key, and safe failure metadata |

A generation record exists from submission onward and is progressively populated. Store images in S3, not as database binary values. Store the durable object key rather than an expiring signed URL. `UserId = null` identifies a generation not assigned to an account.

Retain successful account and guest generations indefinitely in the MVP, including their approved images, transcripts, prompts, and keywords. Do not introduce expiry or automatic deletion of successful guest generations. Expiry of a guest's access does not delete the generation.

Audio is temporary. Delete it after success, rejection, or failure; clean up after interrupted processing as well. Storage settings and cleanup must not inadvertently preserve an audio archive. This describes data under application control; provider-side retention must be reviewed separately.

Retention of failed-job data and rejected image artifacts has not been decided. Do not infer indefinite retention for those artifacts from the policy for successful generations.

### Access to images and guest results

- S3 objects remain private. The backend checks access before issuing a short-lived signed download URL.
- A guest session is represented by an `HttpOnly` cookie and grants access for 24 hours, including checking status, retrieving a result, and claiming it after authentication.
- The cookie carries an access identifier, not a local list of coloring pages. Do not persist guest history in the browser.
- After the guest session expires, its unclaimed generations remain stored but are no longer accessible or claimable through that session.
- Claiming a generation requires both valid access to the guest result and successful account authentication. Assign it atomically, without generating another image, and end access to that generation through the guest session.
- Account history and download access are restricted to the owning account.

## Authentication

The .NET backend manages accounts and one-time email-code challenges; SES transports the messages. Create an account only after successful email verification, or sign into the existing account for that email.

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

Data Protection keys must persist beyond container replacement and be available to application instances that need to validate existing cookies. Protect the key store and separate environments. The concrete storage mechanism is still open.

SES sender verification and production access in `eu-central-1` are deployment prerequisites for sending codes to arbitrary recipients. The exact domain and sender address remain to be supplied.

## Environments and configuration

- **Local:** Angular and .NET development processes, PostgreSQL in Docker, separate test resources and integration configuration.
- **Online:** One deployed application environment using Elastic Beanstalk, RDS, S3, SES, and Parameter Store.
- Keep environment data, credentials, and cookie-protection keys separate.
- Use .NET User Secrets locally. Store online secrets as Parameter Store `SecureString` values encrypted with KMS.
- Configure Elastic Beanstalk with parameter ARNs so it retrieves values into runtime environment variables. Explicitly refresh the environment after changing parameter values.
- Use IAM roles for application access to AWS resources, with permissions scoped to the required resources.
- Provision secret values separately from ordinary Terraform configuration; referencing their ARNs does not require embedding values in application deployment configuration. `sensitive` alone does not prevent values from entering Terraform state.

## Build and deployment

### CI and application release

- Direct pushes to `main` are the approved university workflow; branches and pull requests are optional. If used, pull requests run builds, tests, linting, and Terraform validation without deployment.
- Every push to `main`, including merges, automatically deploys after checks pass. No manual application-release gate is required for the MVP.
- A multi-stage Docker build compiles Angular and publishes .NET, then packages both into an ASP.NET Core runtime image.
- Publish the image to ECR using an immutable commit-based identity. Elastic Beanstalk deploys the built image through `Dockerrun.aws.json`; do not rebuild application source on the deployment server.
- Authenticate GitHub Actions to AWS using OIDC and short-lived IAM credentials. Scope trust to the intended repository and trusted deployment context. Separate application-deployment and infrastructure roles.

Release sequence: checks and tests → image build → ECR publication → database migrations → Elastic Beanstalk deployment → health and smoke checks.

Serialize deployments to the online environment. A new push must not cancel an in-progress migration or deployment. Smoke checks verify application availability without automatically requesting paid image generation.

Run Flyway migrations from a controlled step in AWS with database access, using the SQL scripts associated with the application release. Migration failure blocks the new application release. Keep migrations compatible with the previous application version so that returning to its image remains possible; application rollback does not automatically roll back database changes. The AWS execution environment and deployment mechanics remain open.

### Infrastructure

Manage infrastructure with Terraform through a separate workflow. Review a plan and explicitly initiate `apply`; an application push must not automatically apply infrastructure changes.

Store Terraform state in private encrypted S3 with versioning and locking against concurrent modifications. Bootstrap the state backend and GitHub OIDC trust once before the workflows depend on them. Define ownership of application-version settings between Terraform and the release workflow to avoid conflicting updates.

## Observability and testing

Send application logs to CloudWatch with seven-day retention. Record generation identifiers, processing stages, elapsed time, model IDs, and safe technical error codes. Exclude audio, transcripts, prompts, email addresses, login codes, credentials, signed URLs, and access tokens. Provider diagnostics must not leak request content through exceptions or HTTP logs.

Include application health checks and basic availability monitoring. Detailed alarm settings remain deployment configuration.

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
- Automatic RDS backups are disabled for the university MVP. Revisit backups before commercial production; do not claim database recoverability from an application rollback or from S3 image storage.
- Deployments or process failures can terminate active generations. Interrupted image-generation calls are not automatically repeated.
- External AI quality and moderation effectiveness require evaluation, and email login depends on delivery.

## Open decisions and prerequisites

| Area | Required decision or validation | Resolve before |
| --- | --- | --- |
| AWS networking and HTTPS | Network topology, RDS access, TLS termination, application domain, proxy handling, and instance/deployment settings | Online infrastructure and deployment implementation |
| Cookie protection | Durable protected Data Protection key store shared as needed across deployments | Online authentication implementation |
| Authentication details | Code format and verifier protection, precise challenge throttling, cookie settings, and guest-session renewal behavior | Authentication implementation |
| Recording and job bounds | Audio formats, upload size/duration, processing deadlines, worker ownership/stale detection, cleanup timing, and concurrency | Generation pipeline implementation |
| AI behavior | Account access, model evaluation, moderation rules, result schema, image dimensions, and handling of transient or ambiguous failures | Real AI integration and acceptance testing |
| Provider data | OpenAI-side retention and applicable processing configuration, separately from deletion in application storage | Sending real users' recordings |
| Failure artifacts | Retention and cleanup of failed-job content and rejected images | Persistent generation and cleanup implementation |
| Migrations and deployment | AWS execution environment for Flyway, deployment serialization, Terraform/release ownership boundaries, and rollback procedure | Release workflow implementation |
| Environment setup | Sender domain, SES production access, repository identity, credentials bootstrap, and Terraform state bootstrap | End-to-end online verification |
| Tooling and browser coverage | Exact compatible tool versions, executable CI commands, and supported browsers beyond initial Chromium CI coverage; follow [development-guidelines.md](development-guidelines.md) | Their dependent implementation slices |

Open entries are not permission to choose a materially different architecture silently. Resolve them through the remaining design stages and record the outcome before dependent work.

## Reference documentation

References were consulted during design; verify compatibility when implementing.

- [Npgsql EF Core 10](https://www.npgsql.org/efcore/release-notes/10.0.html)
- [Flyway Docker](https://documentation.red-gate.com/flyway/reference/usage/flyway-docker) and [PostgreSQL support](https://documentation.red-gate.com/flyway/reference/database-driver-reference/postgresql-database)
- [ASP.NET Core hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)
- [ASP.NET Core cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0) and [Data Protection](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0)
- [OpenAI transcription](https://developers.openai.com/api/docs/models/gpt-transcribe), [text model](https://developers.openai.com/api/docs/models/gpt-6-luna), [moderation](https://developers.openai.com/api/docs/guides/moderation), and [image model](https://developers.openai.com/api/docs/models/gpt-image-2.6-flare)
- [S3 signed URLs](https://docs.aws.amazon.com/AmazonS3/latest/userguide/ShareObjectPreSignedURL.html)
- [SES production access](https://docs.aws.amazon.com/ses/latest/dg/request-production-access.html)
- [Beanstalk parameter integration](https://docs.aws.amazon.com/elasticbeanstalk/latest/dg/AWSHowTo.secrets.env-vars.html) and [Docker deployment](https://docs.aws.amazon.com/elasticbeanstalk/latest/dg/single-container-docker-configuration.html)
- [GitHub Actions OIDC for AWS](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-in-aws)
- [Terraform S3 backend](https://developer.hashicorp.com/terraform/language/backend/s3) and [sensitive data](https://developer.hashicorp.com/terraform/language/manage-sensitive-data)

# ColoringApp — Implementation Roadmap

Status: Approved sequence, scope, and completion criteria
Approval date: 2026-11-14
Flyway migration decision and repository status updated: 2026-11-15
First AWS deployment specification approved: 2026-11-18
Target: Working university MVP by the end of December 2026

Sources: [product.md](product.md), [architecture.md](architecture.md), [development-guidelines.md](development-guidelines.md), and [AGENTS.md](../AGENTS.md).

This roadmap defines implementation order and outcomes. It does not replace approved feature specifications or resolve open architectural decisions. Prepare detailed specs just before the relevant work, using the agreed advanced-model workflow. Later stages deliberately remain less detailed.

## Sequence and working rules

Local foundation → first AWS deployment → guest generation → accounts and claiming → history → university acceptance.

- Every stage includes its own tests, error handling, and documentation updates.
- A stage can consist of multiple small implementation tasks and commits.
- Resolve material prerequisites before starting dependent work. Independent work can continue while a prerequisite is being resolved.
- Substantial implementation requires an approved spec in `docs/specs/`; small changes may use agreed conversation scope.
- Specifications and substantive design revisions use an advanced reasoning model such as GPT-6 Astra. An appropriate model such as Sol or Luna may implement the approved scope.
- The user reviews code and explicitly authorizes commits and pushes. Roadmap or spec approval does not authorize Git publication.
- Direct pushes to `main` are allowed. Once deployment is connected in stage 2, each approved push deploys automatically after CI passes. Terraform application remains separately authorized.
- Do not add deferred production features such as generation quotas, payments, search, or automated RDS backups to these stages.

## Stage 1 — Local foundation and CI

**Goal:** Establish a reproducible local application and a working verification pipeline.

**Scope:**

- Retain the existing .NET API project and create the Angular frontend within the agreed repository layout.
- Connect the frontend to the API and the backend to PostgreSQL running locally in Docker.
- Establish Flyway SQL migrations and run the same scripts locally and in database integration tests.
- Establish development configuration, stable compatible tool versions, formatting, linting, and the initial test setup.
- Add GitHub Actions checks for the projects implemented at this stage.
- Document prerequisites, local startup, and verification commands.

**Prerequisites:** Approve the [foundation specification](specs/001-project-foundation.md) and its tool baseline. The workspace now has a Git repository on `main` and a configured GitHub remote; hosted Actions execution remains to be verified after an explicitly authorized push. Commits and remote publication remain subject to user review and approval.

**Completion criteria:**

- The application starts using the documented instructions.
- Angular communicates with the API, and the backend can reach the local PostgreSQL instance.
- Flyway initializes an empty database, validates the applied migrations, and safely does nothing when rerun without new scripts.
- Initial meaningful checks run locally and in GitHub Actions and pass.
- No AWS deployment is required to complete the local application; the deployment workflow is connected in stage 2.

**Dependencies:** Approved design documents and foundation spec.

## Stage 2 — First AWS deployment

**Goal:** Verify the deployment path early with the application foundation.

**Scope:**

- Define the required AWS resources with Terraform in `eu-central-1`, including application hosting, RDS, S3, Parameter Store references, ECR, and IAM permissions.
- Bootstrap protected Terraform state and GitHub Actions OIDC access.
- Configure the application domain, HTTPS, environment configuration, and CloudWatch logging with seven-day retention.
- Build the shared frontend/backend image in CI, publish it to ECR, and connect automatic Elastic Beanstalk deployment after successful checks.
- Establish the controlled Flyway migration step and deployment health/smoke checks.
- Check OpenAI model access and SES readiness early to expose integration blockers before feature delivery.

**Prerequisites:** Follow the approved decisions in [specification 002](specs/002-first-aws-deployment.md). Verify exact tool/platform versions, regional availability, required IAM access, and secret provisioning before their dependent online steps. AWS account/profile access and the Route 53 delegation were verified on 2026-11-18. Infrastructure changes require explicit authorization.

**Completion criteria:**

- The application foundation is reachable online over HTTPS.
- After a reviewed and authorized push, successful CI publishes and deploys the corresponding image, and health checks pass.
- The deployed backend can reach RDS; runtime secrets are absent from repository files and the image.
- Logs reach CloudWatch with the agreed retention.
- OpenAI and SES access findings are known; outstanding service-access prerequisites are recorded before their dependent stages.

**Dependencies:** Stage 1 and an approved deployment specification.

## Stage 3 — Guest voice-to-coloring flow

**Goal:** Deliver the product's main user journey without requiring an account.

**Scope:** Voice recording, temporary private audio storage, guest access, persistent generation jobs, background processing, the full OpenAI sequence, image verification, result storage, polling, preview, PNG download, and printing.

**Prerequisites:** Resolve the input formats and technical bounds, job deadlines and ownership, stale-job handling, guest-session details, moderation rules, provider-side retention, image dimensions, and cleanup/retention of failed attempts and rejected outputs. Confirm model access and evaluate the chosen models on representative inputs.

**Completion criteria:**

- A guest can record a permitted Polish description and obtain a previewable, downloadable, printable coloring page.
- Input and output policy rejections and technical failures produce the appropriate messages without exposing rejected images.
- Status and result access require the proper guest authorization and obey the 24-hour access policy.
- Successful guest generations retain the required text, keywords, and image indefinitely.
- Audio is deleted after terminal outcomes, and orphan cleanup is verified.
- Pending jobs survive restart; abandoned in-progress jobs fail safely without blindly repeating image generation.
- Automated tests exercise the important success, access-control, concurrency, and failure paths; real integrations are checked separately.

**Dependencies:** Stages 1–2, ready OpenAI integration, and an approved generation specification.

## Stage 4 — Accounts and saving the current result

**Goal:** Let users verify their email, sign in, and keep the coloring page that prompted registration.

**Scope:** Backend-managed accounts and email codes, SES delivery, authenticated cookies, logout, and atomic assignment of an existing guest result to an account.

**Prerequisites:** Confirm the SES sender and production access needed for arbitrary recipients, implement persistent protected cookie keys, and resolve the remaining challenge and session details from the architecture.

**Completion criteria:**

- Email verification creates a new account or signs into the existing account, following the approved validity, attempt, and resend rules.
- A successful save flow assigns the exact existing guest generation to the authenticated account without regenerating it.
- Subsequent authenticated generation is associated with the account; the history UI follows in stage 5.
- Logout and fixed seven-day authenticated session expiry work as specified.
- Tests cover expiry, single consumption, authorization, concurrent verification/claiming, and failures.

**Dependencies:** Stage 3, ready SES integration, and an approved authentication/claiming specification.

## Stage 5 — Account history

**Goal:** Make saved coloring pages useful on later visits.

**Scope:** The current user's successful generations, with thumbnails, dates, and recognized descriptions; opening, downloading, and reprinting stored images.

**Completion criteria:**

- A user sees only their own successful generations, including claimed guest results and later authenticated generations.
- Results remain available after signing in again.
- Opening, downloading, and printing use stored images without another AI generation call.
- Empty history and relevant error/access states are handled.

**Dependencies:** Stage 4 and an approved history specification.

## Stage 6 — University acceptance and demonstration

**Goal:** Confirm that the delivered application satisfies the agreed MVP.

**Scope:** End-to-end verification, real provider checks, supported desktop/mobile browser checks, microphone and email testing, printing, defect correction, and startup/demo documentation.

**Completion criteria:**

- Verify the acceptance criteria in [product.md](product.md) against the deployed application.
- Confirm real microphone use, Polish transcription, moderation, coloring-page quality, email delivery, and printing on target devices.
- Required automated checks pass; failures discovered during acceptance are resolved or explicitly discussed with the user before claiming completion.
- Documentation describes setup, the demonstration flow, and known limitations.
- The project remains within the approved university scope. Production backups and other deferred features are not silently introduced.

**Dependencies:** Stages 1–5. Tests are developed throughout those stages; this stage verifies the integrated result.

## Current implementation and next task

The foundation is implemented in commit `caccd62`. Review on 2026-11-16 confirmed successful backend and frontend builds, 9 backend tests, 9 frontend tests, and 4 Chromium scenarios. Hosted GitHub Actions verification remains pending in the available evidence, as recorded in [specification 001](specs/001-project-foundation.md).

Implement [specification 002 — First AWS Deployment](specs/002-first-aws-deployment.md), approved on 2026-11-18. It defines production packaging, the AWS topology, Flyway execution, secret and key storage, and release automation for stage 2. Verify remaining implementation prerequisites before the affected online steps. The existing explicit approval boundaries apply to Git publication and infrastructure application.

## Readiness and open decisions

The project-level documents are sufficient to prepare and review the foundation specification. They are not a complete implementation contract for every future feature. The open-decision table in [architecture.md](architecture.md) remains authoritative; each stage above identifies the prerequisites relevant to it.

Choose supported target browsers and concrete quality/timeout checks with the relevant feature specs. A Chromium CI run alone does not verify mobile microphone behavior or printing. Review current model availability during integration rather than assuming that design-time documentation proves account access.

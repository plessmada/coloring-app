# ColoringApp — Development Guidelines

Status: Approved for the university MVP
Approval date: 2026-11-14
Flyway migration decision incorporated: 2026-11-15
Related documents: [product.md](product.md), [architecture.md](architecture.md)

These standards prioritize simple, fast delivery of the university project. Revisit the workflow before commercial production. Product scope and architectural decisions remain defined by the linked documents; these guidelines do not resolve their explicitly open decisions.

## Repository and code organization

| Location | Responsibility |
| --- | --- |
| `backend/` | Existing .NET solution, API application, and separate backend test projects |
| `frontend/` | Angular application, with unit tests next to the code they exercise |
| `docs/` | Product, architecture, engineering standards, and subsequent planning documents |
| `docs/specs/` | Feature specifications, added when substantial features are designed |

Organize frontend and backend code by feature, such as authentication, generations, and history. Keep one backend application. Add shared components when concrete reuse warrants them.

Use EF Core directly in feature services. Define interfaces at integration boundaries such as OpenAI and email delivery so that these dependencies can be replaced in tests. Introduce additional abstractions when they solve a demonstrated problem.

Keep endpoint handlers short: validate input, enforce access, call the appropriate service, and map the response. Keep the generation workflow in its service, called by the background worker. Angular components handle presentation; services handle API communication.

## Feature specifications and agent workflow

Use a repository specification for a substantial new user flow or significant behavior, data-model, or integration change. Small fixes, copy or layout changes, and minor adjustments can proceed from the agreed conversation scope, with clarification where the outcome is ambiguous.

Place feature specs in `docs/specs/` with a numbered English filename, such as `001-voice-generation.md`. Add individual specs when the relevant feature is being designed; do not generate all future specs in advance.

Each spec contains:

1. Goal, scope, and exclusions.
2. User-visible behavior, including failures.
3. API, data, and integration changes where relevant.
4. Acceptance criteria and verification approach.
5. Material decisions and open questions.
6. A short implementation plan and status: `Draft`, `Approved`, or `Implemented`.

Reference shared product, architecture, and engineering documents instead of copying them. Resolve material feature decisions before marking a spec approved. Record the user's approval; the agent must not approve its own draft.

Workflow: discussion → draft spec → user approval → autonomous implementation and tests → user code review → explicit approval for commit and/or push. Spec approval authorizes implementation of the agreed feature, not Git publication. If the agreed behavior or architecture needs to change, obtain approval for the revision before implementing it. Keep the spec aligned with the approved outcome; mark it implemented only when its acceptance criteria and Definition of Done are met.

### Model selection for development

- Author feature specifications and substantive design revisions using an advanced reasoning model, such as **GPT-6 Astra**, or a comparable advanced model chosen by the user.
- Implement approved specifications using a model appropriate to the task, such as **Sol** or **Luna**. Use the approved criteria and verification to assess the result regardless of the model.
- Return material design questions discovered during implementation to specification review using an advanced model.
- Model choice is configured for the development session. `AGENTS.md` describes the policy but does not automatically change the active model. Do not claim a model was selected or verified when that cannot be established.
- This policy concerns development agents; application-runtime models remain governed by [architecture.md](architecture.md).

## Language, naming, and formatting

- Write code identifiers, filenames, comments, technical documentation, and commit messages in English.
- Write user-facing interface messages in Polish. Discuss work with the user in Polish.
- Follow conventional C# naming: `PascalCase` for types and methods, `camelCase` for parameters and local variables.
- Use `kebab-case` for Angular source filenames and conventional TypeScript naming for types, members, and variables.
- Use descriptive names tied to the feature's purpose.
- Explain non-obvious reasons and tradeoffs in comments.

| Scope | Tooling |
| --- | --- |
| C# | `.editorconfig` and `dotnet format` |
| Angular | ESLint with Angular support and Prettier |

Keep formatter and linter configuration in the repository. Automated checks verify formatting; local tools can apply it. Avoid unrelated formatting churn in feature changes.

## C# and TypeScript

- Enable TypeScript strict type checking and C# nullable reference types.
- Use explicit request and response DTOs. EF Core entities are not the public API contract.
- Use asynchronous C# APIs for I/O and pass `CancellationToken` where cancellation is appropriate.
- A durable background generation must not depend on the browser keeping its request open. Use job and application lifecycle cancellation according to the architecture's interruption policy.
- Keep responsibility boundaries clear: presentation in components, HTTP access in frontend services, and application workflows in backend feature services.

## Validation and errors

The backend is authoritative for validation and authorization. Frontend validation provides timely feedback but does not replace server checks.

Follow the error contract in [architecture.md](architecture.md): `ProblemDetails` and stable codes for request failures, job status and codes for processing outcomes, and Polish messages selected by the frontend. Preserve the distinction between unusable input, content rejection, and technical failure. Do not expose internal exceptions, provider payloads, or secrets in responses.

## Dependencies and versions

- Use npm for the frontend and commit `package-lock.json`.
- Record compatible SDK, runtime, tool, and dependency versions in project configuration and applicable lockfiles. Use reproducible dependency restoration in CI.
- Choose stable compatible releases. Resolve exact versions when setting up the relevant project or tooling; retain the approved .NET 10 baseline.
- Add dependencies for concrete requirements, considering existing framework capabilities first.
- Treat dependency upgrades as deliberate changes with appropriate verification. Avoid unrelated major upgrades during feature work.
- Keep secrets outside the repository, following the environment configuration in [architecture.md](architecture.md).

## Database migrations

Use Flyway versioned SQL migrations as the schema source of truth. Keep EF Core mappings consistent with those scripts and exercise the same migrations against real PostgreSQL in integration tests. Do not create a separate test schema through EF Core migrations or `EnsureCreated()`.

Do not edit an applied migration. Add a subsequent versioned script and run Flyway validation. Stop on migration or checksum errors. Do not automatically use `repair`, `clean`, or baselining to suppress failures.

## Testing

| Scope | Approved tool |
| --- | --- |
| Backend tests | xUnit |
| PostgreSQL integration tests | Testcontainers for .NET |
| Angular unit tests | Vitest |
| Browser scenarios | Playwright, initially Chromium in CI |

Add tests with features and meaningful bug fixes. Focus on observable behavior, important boundaries, and error handling. There is no mandatory code-coverage percentage.

Priority scenarios include generation state transitions, code expiry and single use, account isolation, guest access and claiming, persistence, concurrent claims, moderation decisions, and failures. Use real PostgreSQL for database integration tests and controlled substitutes for OpenAI and email delivery in automated tests.

Keep a small set of browser tests covering guest generation, authentication and saving a result, account history, and errors. Check responsive UI behavior in the browser. Separately verify actual microphone recording, Polish transcription, image quality, real email delivery, and printing on target devices.

Run checks appropriate to the change and report what ran. If a required check cannot run, explain the blocker and remaining uncertainty rather than claiming verification. Chromium CI coverage does not by itself establish support for all target browsers.

## Git workflow and review

- During the university phase, work directly on `main`; feature branches and pull requests are optional.
- Use short, specific English commit messages. Conventional Commits are not required.
- Keep each commit focused on one understandable task or correction.
- CI may temporarily fail on `main`. Fix the issue in a subsequent commit.
- Review the diff for scope, accidental changes, and sensitive data before considering the work complete. Mandatory PR approval is not part of the MVP workflow.
- Revisit branch protection and formal code review before commercial production.

Agents must leave changes for the user's review and obtain explicit approval before committing or pushing. Approval applies to the reviewed changes and named actions. See [AGENTS.md](../AGENTS.md) for agent responsibilities. Design-only tasks cover authorized documentation changes. Approved feature specifications authorize implementation within their scope.

## CI expectations

Pushes to `main` and pull requests run the applicable build, lint, format, and automated test checks. Use disposable databases and controlled substitutes for AI and email in CI.

Introduce executable checks as their projects and features are implemented. A failure unrelated to a feature must be reported; an unverified change cannot be described as passing the full pipeline.

## Definition of Done

A feature is complete when:

1. Its agreed acceptance criteria are satisfied.
2. Relevant tests, builds, and style checks pass.
3. Important error paths are handled.
4. UI changes have been checked in a browser, including a mobile layout.
5. Documentation reflects the resulting behavior.
6. The completion report explains the change, verification performed, and remaining limitations.

Temporary red CI is permitted while work is in progress. Resolve failures associated with the task before declaring it complete. Do not report a deployment as successful until its result has been observed.

## References

- [xUnit](https://xunit.net/docs/getting-started/v3/getting-started)
- [Testcontainers PostgreSQL module](https://dotnet.testcontainers.org/modules/postgres/)
- [Angular testing with Vitest](https://angular.dev/guide/testing)
- [Playwright](https://playwright.dev/docs/intro)
- [dotnet format](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format)
- [Angular ESLint](https://github.com/angular-eslint/angular-eslint)
- [Prettier](https://prettier.io/docs/index.html)

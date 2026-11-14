# ColoringApp — Agent Instructions

## Read the context

- Read [product.md](docs/product.md), [architecture.md](docs/architecture.md), [development-guidelines.md](docs/development-guidelines.md), and [roadmap.md](docs/roadmap.md) before changing the project. Read the relevant approved feature spec in `docs/specs/` when one exists.
- Inspect the existing implementation and working-tree changes. Preserve user changes and keep work within the requested scope.
- Treat the product document as the source of product scope, architecture as the source of technical decisions, development guidelines as the source of engineering standards, and the roadmap as the source of implementation order. Specs define approved feature details. Raise conflicts and unresolved decisions rather than silently overriding a source.
- Discuss work in Polish. Write code, identifiers, comments, specs, and technical documentation in English; keep application UI text in Polish.

## Specify and implement

- For a substantial new user flow or significant behavior, data-model, or integration change, prepare a concise `Draft` spec in `docs/specs/` using the format in the development guidelines. Obtain the user's approval before implementation.
- Author and substantively revise feature specs with an advanced reasoning model, such as GPT-6 Astra. Implement approved specs with a suitable model such as Sol or Luna. Model selection belongs to the session configuration; this file does not switch models. If the active session is known to use an unsuitable model for specification work, request a model change before that work. Do not claim to have verified or changed an unknown model.
- For small changes, use the agreed conversation scope and clarify only uncertainties that affect the result. A separate spec is not required.
- After spec approval, work autonomously within its scope. Choose names, file organization, and implementation details consistent with the shared documents. Use the spec's short implementation plan rather than duplicating it in another planning document.
- If work requires a product or architecture change, or resolution of a material open decision, explain it and obtain approval before the affected implementation. Update the relevant spec and shared documents to reflect approved decisions.
- During design-only tasks, make only authorized documentation changes. Approval of the project documents does not by itself start implementation.

## Verify and report

- Follow the coding, validation, dependency, and test standards in [development-guidelines.md](docs/development-guidelines.md). Discover actual project commands from repository configuration; do not invent successful checks or add unrelated changes to make checks pass.
- Run relevant builds, tests, and style checks. Inspect UI changes in a browser, including a mobile layout. Use controlled AI and email substitutes in automated tests.
- Check acceptance criteria, error paths, authorization, and the diff. Report unavailable checks and remaining uncertainty.
- Finish with a concise report of changes, verification results, and limitations. Leave code available for the user's review.

## Approval boundaries

- Do not commit or push until the user has reviewed the code and explicitly approved the corresponding action for those changes. Approval of a spec or implementation task is not Git approval. A commit-only approval does not authorize pushing.
- Work directly on `main` for the university MVP; branches and PRs are optional. An approved push to `main` triggers the agreed automatic deployment after CI passes. Do not bypass failing checks.
- Obtain explicit authorization for destructive operations or manual changes to cloud infrastructure, including `terraform apply`. Existing approval for the concrete action remains valid; do not repeatedly ask for it.
- Preserve data, secrets, and unrelated work. Do not broaden a task into unrelated refactoring or dependency upgrades.

The user controls product and architecture decisions and reviews code before Git publication. Keep routine implementation autonomous inside those boundaries.

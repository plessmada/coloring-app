# 002 — First AWS Deployment

Status: Approved
Created: 2026-11-16
Approval: Approved by the user on 2026-11-18 for implementation. Git publication and infrastructure application require the separate authorization defined in AGENTS.md.
Roadmap stage: 2 — First AWS deployment

Sources are the [product](../product.md), [architecture](../architecture.md), [development guidelines](../development-guidelines.md), [roadmap](../roadmap.md), [foundation spec](001-project-foundation.md), and [agent instructions](../../AGENTS.md).

## Goal and scope

Make the implemented foundation available at a public HTTPS address in `eu-central-1`. Establish a repeatable release path from successful GitHub Actions checks through Flyway migration to a verified Elastic Beanstalk deployment. Provision infrastructure with Terraform and preserve the user's separate approval for infrastructure application and Git publication.

This slice includes production application packaging, AWS infrastructure, database initialization, secret references, persistent Data Protection keys, release automation, rollback instructions, and deployment verification. Generation, recording, account flows, and history follow in their existing roadmap stages. OpenAI and SES work in this slice establishes provider readiness for those stages.

## Implementation review and evidence

The review used commit `caccd626034e86e079f725bb039bd902bc5d6213` on `main`. The working tree was clean before this documentation change.

| Area | Current implementation and consequence for this slice |
| --- | --- |
| Backend | .NET 10 Minimal API with EF Core/Npgsql, sanitized health responses, and bounded database checks. Reuse both health contracts. |
| Frontend | Angular 22.2.6 uses relative `/api/health/ready`, with loading, ready, failure, and retry states. The production build produces `frontend/dist/coloring-app/browser/`. |
| Database | PostgreSQL `18.7`, Flyway `13.12.2`, V1 creating `coloring_app`, history in `public.flyway_schema_history`. Reuse the SQL and shared Flyway settings. |
| Production hosting gap | `Program.cs` maps API endpoints and enables HTTPS redirection outside Development. Static frontend serving and trusted forwarded-header handling still need implementation. |
| Packaging gap | Application Dockerfile, deployment bundle, and populated Terraform configuration are still to be added. Local Compose currently runs PostgreSQL and Flyway. |
| Verification gap | API and browser fixtures explicitly select Development. Add checks of the published Production image and its proxy behavior. |
| CI | `checks.yml` runs the existing checks on pushes to `main` and pull requests. AWS identity, migration, and release jobs still need implementation. |

Verification repeated on 2026-11-16 passed the backend build, all 9 backend tests with disposable PostgreSQL/Flyway, the frontend production build, lint, formatting, all 9 Vitest tests, and all 4 desktop/mobile Chromium scenarios. Docker Engine `29.9.3` was accessible outside the agent sandbox. Backend formatting and persistent-volume restart checks are recorded in spec 001's implementation report.

Hosted CI for this commit remains unverified in this review. GitHub CLI had no authenticated session, and the connector's combined-status response contained no statuses. Spec 001 still records hosted verification as pending. AWS CLI and Terraform were absent from the agent's PATH. AWS account resources, regional availability, domain ownership, and provider access have not been inspected.

## Approved deployment decisions

The following choices were approved on 2026-11-18 and resolve the corresponding open architecture items for this slice.

| Concern | Approved choice |
| --- | --- |
| Environment | One environment named `coloring-app-mvp` in `eu-central-1`, resource tags identifying the project and environment |
| Network | Dedicated VPC with two public, two private application, and two isolated database subnets across two availability zones |
| Internet access | One NAT gateway for application and migration egress, accepting its single-AZ dependency for the university environment |
| HTTPS | Public Application Load Balancer, ACM certificate in `eu-central-1`, and hostname `color.plik.click` supplied by the user on 2026-11-18 |
| DNS | Public Route 53 hosted zone `color.plik.click`, delegated through four NS records in the parent Cloudflare zone, selected by the user on 2026-11-18 |
| Application | Elastic Beanstalk Docker on a supported AL2023 platform, pinned platform version, x86-64 `t3.small`, one steady-state instance |
| Updates | Immutable application deployments, allowing temporary additional capacity while a new version becomes healthy |
| Database | Independently managed RDS PostgreSQL 18, `db.t4g.micro`, Single-AZ, 20 GiB encrypted gp3 storage, automated backup retention `0` |
| Migration execution | VPC-connected CodeBuild project running the existing pinned Flyway container against private RDS |
| Data Protection | AWS SSM provider, environment-specific Parameter Store path, KMS protection, stable application name |
| Infrastructure state | Private encrypted versioned S3 backend with native S3 locking through `use_lockfile` |
| Release identity | Immutable Git commit identifier, ECR image digest, and versioned S3 deployment/migration artifacts |

Select and record exact compatible Terraform, AWS provider, Elastic Beanstalk platform, CodeBuild image, and container base versions during implementation. Preserve existing application dependency pins. Use Terraform supporting ephemeral variables, write-only RDS password arguments, and S3 locking, with a compatible pinned AWS provider and committed provider lockfiles.

Confirm the selected instance classes and PostgreSQL 18 engine availability in the target account and region before applying infrastructure. Prefer the current local minor version `18.7` when offered by RDS. Record an available PostgreSQL 18 minor explicitly and exercise migrations against that version when RDS availability differs. A major engine change requires a spec revision.

ALB, NAT, RDS, and temporary immutable-deployment capacity incur ongoing AWS charges. The approved design follows the user's preference for a working university environment with budget optimization deferred.

## User-visible and HTTP behavior

The existing Polish foundation screen loads at the selected HTTPS origin and reaches readiness through that same origin. The failure and retry behavior from spec 001 applies when the API or database is unavailable. A release smoke check requires the ready state before reporting success.

| Request | Required result |
| --- | --- |
| Public HTTP request | ALB redirects to the equivalent HTTPS URL, preserving path and query |
| `GET /` over HTTPS | `200` with the compiled Angular entry page |
| Hashed frontend asset | Correct content and content type, with long-lived immutable caching |
| Angular entry document | Revalidation or no-cache behavior so a deployment can load new asset names |
| Existing health routes | Existing JSON contracts and `Cache-Control: no-store` |
| Unknown `/api/...` route | API `404`, with the request excluded from the SPA fallback |
| Browser navigation to an application route | SPA entry document, restricted to appropriate HTML navigation requests |
| Missing static asset | `404` |

Serve the frontend from the API's published `wwwroot`. Use ASP.NET Core static-file and fallback handling with tests for route precedence. Retain the local development proxy workflow.

TLS terminates at the ALB, followed by the Beanstalk nginx proxy and the application container on port `8080`. Configure forwarded headers before scheme-dependent middleware, using explicitly trusted proxy networks/addresses and the actual proxy hop count. Preserve the original HTTPS scheme through nginx. Verify that untrusted forwarded headers are ignored. Set allowed hosts for the selected hostname and support internal health probes deliberately.

Keep an internal HTTP health path for load-balancer checks. Use `/api/health` for instance liveness and use `/api/health/ready` as the database-dependent release check. Document and test redirect behavior so internal probes receive their expected response and public HTTPS requests avoid redirect loops.

## Application image and release artifacts

- Add a multi-stage root `Dockerfile` using the pinned Node/npm and .NET SDK. Restore dependencies from committed locks, build Angular for production, publish only the API application, and copy the browser output into `wwwroot`.
- Run a compatible .NET 10 runtime image as a non-root user on port `8080` with `ASPNETCORE_ENVIRONMENT=Production`. Record exact base tags or digests and build for `linux/amd64` to match Beanstalk.
- Add `.dockerignore` rules covering `.git`, `.env` files, credentials, Terraform working files/state/plans, dependencies, test output, and host build output. Runtime secrets enter through environment configuration.
- Test the built image against disposable PostgreSQL initialized by the existing Flyway runner. Build once and publish that tested image to ECR. On a rerun, reuse a previously published immutable image for the commit and verify its identity.
- Produce a Beanstalk source bundle with `Dockerrun.aws.json` v1 referencing the ECR image by digest, plus required proxy/platform configuration. Set container port `8080`. The bundle instructs Beanstalk to pull the tested image.
- Produce a migration bundle from the same checkout containing the Flyway configuration, SQL files, and migration runner definition. Record its S3 object version, commit SHA, and checksum alongside the application digest.
- Keep release artifacts under commit-specific keys and enable ECR tag immutability. Retain the active and preceding successful releases for rollback. Configure artifact cleanup only where retained release references remain valid.

## AWS resources and access

### Network and database

The ALB accepts public ports 80 and 443. Application instances reside in private subnets, accept traffic from the ALB security group, and reach AWS services and OpenAI through NAT. CodeBuild uses private subnets with NAT access for its runtime dependencies. Use a pinned compatible Linux CodeBuild image and enable privileged mode for its Flyway Docker runner. RDS has `publicly_accessible=false` and accepts PostgreSQL traffic only from application and migration security groups. Provide outbound access and DNS resolution required by those workloads. CodeBuild requires no inbound rules.

Manage RDS independently of the Beanstalk environment to preserve it across application-environment replacement. Enable storage encryption and explicit deletion protection for RDS and persistent buckets. Apply the existing explicit-authorization boundary to resource removal or replacement. Keep the agreed RDS automated backup retention at zero.

Require certificate-verified TLS to RDS. Include the AWS RDS CA bundle in the application and migration runtime and configure Npgsql `VerifyFull` and JDBC `verify-full` with the RDS endpoint as the server name. Keep local and disposable test database TLS settings explicit for their environment.

Use separate PostgreSQL migration and runtime roles. A controlled initial database-administration job creates these roles and grants the migrator the database/schema privileges required by V1 and Flyway history. The migrator owns application schema objects. After the first migration creates `coloring_app`, the administrative path grants runtime schema usage and the data privileges needed by implemented features, including default privileges for future objects created by the migrator. Verify that runtime credentials cannot alter schema objects or Flyway history.

Keep role bootstrap and its master credentials in a separately authorized administrative path. Ordinary release jobs can invoke only the migration project. Bootstrap operations must be safe to rerun without resetting existing roles, schema, or data. Keep original V1 intact. Add a numbered migration only if implementation introduces additional database schema changes.

### Storage, secrets, and keys

Provision separate private S3 buckets for Terraform state, release artifacts, generated images, and temporary audio. Block public access and require encrypted transport. Version state and release artifacts. Preserve generated images indefinitely. Configure temporary-audio storage so deletion can remove the object fully, with lifecycle cleanup defined alongside the generation flow in stage 3. Stage 2 verification uses synthetic objects under a dedicated probe prefix.

Store secrets in Parameter Store `SecureString` under `/coloring-app/mvp/`, encrypted using an environment KMS key. Terraform manages IAM, encryption, and references. Provision secret values through a controlled bootstrap procedure. For RDS creation, supply the master password using an ephemeral input to the provider's write-only password argument and record its version counter. Avoid Terraform data sources that persist decrypted parameter values. Review generated state and plan handling for secret exposure.

| Consumer | Secret/configuration contract |
| --- | --- |
| API | `ConnectionStrings__DefaultConnection` resolves from a Parameter Store ARN through Beanstalk's `application:environmentsecrets` namespace and uses runtime DB credentials |
| Migration CodeBuild | JDBC endpoint and database name plus Parameter Store references for migrator username/password |
| Database bootstrap | Restricted master and initial role credentials, accessible only to the administrative runner and bootstrap operator |
| Data Protection | `/coloring-app/mvp/data-protection` key path, stable application name `ColoringApp-mvp`, KMS key ARN |
| Future AI integration | Reserved parameter naming and access requirements recorded with provider readiness findings |

Use `Amazon.AspNetCore.DataProtection.SSM` with a compatible pinned version. Configure KMS protection and retain keys across application replacements. Scope key access to the environment path. Verify that a protected synthetic payload can be unprotected after replacing the process/container. Run this verification through a test harness with AWS access, keeping diagnostic operations out of the public API. Local tests use an isolated substitute.

Refresh the Beanstalk environment explicitly when externally provisioned parameter values change. Document this separately from deploying an application version. Redact credentials, key material, signed URLs, and sensitive provider content in bootstrap, application, and CodeBuild logs.

### IAM and identity

Use GitHub OIDC with `aud=sts.amazonaws.com` and trust scoped to `plessmada/coloring-app` on `refs/heads/main`. Grant OIDC permission only to trusted release or infrastructure jobs. Pull-request jobs run credential-free checks.

Use separate roles for application releases, infrastructure changes, Beanstalk instances, normal migrations, and database bootstrap. Scope ECR publication, artifact access, CodeBuild start/poll, and Beanstalk updates to the relevant resources. Restrict `iam:PassRole` to explicitly required roles and AWS services. The normal release role can access release metadata and artifacts while runtime/database secret values remain with their designated workload roles.

AWS instance credentials must work from the container using IMDSv2 and an explicitly verified metadata hop limit suitable for Docker. Confirm the resulting AWS identity from the deployed workload during acceptance. Keep CloudWatch log retention at seven days for application, Beanstalk, migration, and bootstrap logs.

## Terraform structure and first deployment

Keep the implementation small with `infra/bootstrap/` and `infra/environments/mvp/` roots, shared only where concrete reuse warrants it. The bootstrap root creates the state backend and scoped GitHub OIDC infrastructure access, and imports the existing public Route 53 hosted zone `color.plik.click` with ID `Z0372976IMP84ITPTSI2`. Preserve its assigned nameservers and output its zone ID and delegation set. The environment root owns networking, data resources, IAM, CodeBuild, Beanstalk configuration, and application/certificate DNS records within that zone. Record exact inputs and outputs in `infra/README.md`.

The domain owner adds four NS records named `color` in Cloudflare, using the exact nameservers assigned to the Route 53 zone. Confirm public delegation before waiting for ACM DNS validation. Terraform manages ACM validation records and an apex A alias for `color.plik.click` targeting the ALB in the delegated Route 53 zone. Inspect any existing records at or beneath the delegation before migration and preserve their intended behavior. Protect the hosted zone from accidental replacement because replacement can change the assigned nameservers.

Use this order to resolve first-deployment dependencies.

1. Confirm AWS account identity, region, hostname/DNS ownership, and tool versions. Prepare non-secret input examples.
2. Review and explicitly authorize bootstrap infrastructure application. Use temporary operator credentials, establish encrypted/versioned state storage and locking, and migrate bootstrap state into its own remote key. Reuse an existing account OIDC provider when applicable. Import the existing public `color.plik.click` hosted zone `Z0372976IMP84ITPTSI2` and verify that the plan preserves its assigned nameservers and the completed Cloudflare delegation.
3. Plan and explicitly apply core environment resources with application creation disabled. Create networking, RDS, ECR, buckets, IAM, KMS, migration/admin runners, and certificate resources. Provide the RDS master password through the ephemeral/write-only path.
4. Provision the required SecureString values and complete controlled database-role bootstrap. Use RDS outputs to construct runtime and migration configuration without exposing credentials. Complete certificate validation and the required DNS records.
5. Run the ordinary verification/package path for an exact commit. Publish its tested application image and release/migration artifacts, then run Flyway in CodeBuild and wait for success. Finalize runtime schema/default privileges through the authorized administrative path and verify runtime access before starting the application.
6. Review and explicitly apply the environment plan enabling Beanstalk with that initial application version and its runtime secret references. Observe HTTPS, readiness, and deployed identity checks.
7. Enable the automatic main-branch release path and verify a subsequent reviewed, authorized push. Record the first successful run and deployed commit.

Expose application creation as an explicit bootstrap variable with documented safeguards after initialization. Terraform owns environment settings, roles, certificates, networking, and logging. The release workflow owns Beanstalk application versions and subsequent environment `version_label` changes. Ignore only that release-owned attribute in Terraform after initialization. A later infrastructure plan must preserve the deployed application version.

Infrastructure automation uses an explicitly invoked workflow. It produces a reviewable plan and applies that exact approved plan. Use the project's existing explicit authorization boundary for application of infrastructure. Keep ephemeral password inputs available again when applying a saved plan and protect plan artifacts. Run `terraform fmt -check` and initialization/validation without deployment credentials on ordinary CI.

## Automatic application release

Extend the existing checks workflow, preserving its current checks. A release must use the exact commit whose checks passed. Gate AWS jobs to pushes on `main`, with automatic release after the bootstrap environment is ready.

1. Pass foundation checks, Terraform static validation, and production-image tests.
2. Publish the tested image and matching bundles, or verify immutable existing artifacts on a rerun.
3. Start the migration CodeBuild project with the exact versioned migration artifact. Wait for completion and verify the build result. CodeBuild executes `migrate` with validation enabled, then `validate`.
4. Register the Beanstalk application version and update the environment only after migration succeeds.
5. Wait for the target version and healthy environment state. Verify the public HTTPS page, asset loading, liveness, readiness, and a release identifier equal to the intended commit. Expose a non-secret build identifier in a response header for this check while preserving health JSON contracts.
6. Record the commit, image digest, bundle version, migration result, Beanstalk version, and smoke result in the workflow summary.

Serialize the main-branch release sequence through one concurrency group with `cancel-in-progress: false` and queued pending releases. Use the documented `queue: max` behavior and verify support in the repository. Preserve the active migration/deployment on a newer push. Detect an already deployed newer commit and report an older delayed run as superseded, preserving forward release order. Infrastructure application and authorized rollback use the same environment mutation exclusion.

Give CodeBuild a finite execution timeout and ensure the workflow observes its terminal state. On workflow interruption or timeout, inspect the actual AWS build and environment state before starting another release. Keep migrations idempotent at the Flyway history level. Never infer that canceling a GitHub job stopped an AWS operation.

For a migration failure, fail the release and keep the previous application version active. For an application deployment or smoke failure, report the actual deployed version and health. Beanstalk's immutable deployment handles failed replacement instances. Provide an explicitly initiated rollback workflow for selecting a retained successful version by identity and verifying it after deployment. A database schema already changed by migration remains subject to the architecture's backward-compatibility requirement.

Application release smoke checks exercise health and the foundation UI. Provider availability checks are handled separately below.

## Provider readiness and deployment inputs

Before dependent provider work, record the following in the deployment runbook.

- OpenAI project/key availability, configured model identifiers, and access findings from supported metadata/account checks. Explicitly distinguish confirmed access from permissions that require a real request during stage 3.
- SES identity and sender configuration in `eu-central-1`, domain verification/DKIM status, and sandbox or production-access status. Record remaining prerequisites for sending codes to arbitrary recipients before stage 4.
- Regional service availability, selected platform/engine versions, and account quotas relevant to ALB, EC2 immutable updates, RDS, NAT, and CodeBuild.

| Input | Current state and required timing |
| --- | --- |
| AWS account ID and operator profile | Verified on 2026-11-18 through STS using profile `coloring-app`, account `613324878297`, configured region `eu-central-1`. The current session identity is `arn:aws:iam::613324878297:root` |
| Public hostname and DNS provider | The user selected `color.plik.click` and delegation of this subdomain to Route 53 on 2026-11-18. Public NS lookup for the parent domain returned Cloudflare nameservers `ruth.ns.cloudflare.com` and `nero.ns.cloudflare.com` |
| DNS management | Existing public Route 53 zone `Z0372976IMP84ITPTSI2` and parent delegation verified on 2026-11-18. Import the zone into Terraform and manage application and ACM validation records within it |
| Repository identity | `plessmada/coloring-app`, branch `main` |
| GitHub Actions access | Hosted foundation verification and repository settings access need confirmation |
| OpenAI access and SES sender domain | To inspect with user-provided account configuration before their dependent integration stages |

The user approved these architecture choices on 2026-11-18. The implementation agent can prepare code and local verification, using verified deployment inputs and variables for remaining environment values. The affected online steps require the actual values and existing explicit authorization for cloud mutations.

### AWS access and DNS verification — 2026-11-18

AWS CLI `2.41.0` is installed. STS confirmed the account and session identity above. Route 53 returned the public zone `color.plik.click` and these four nameservers:

- `ns-42.awsdns-05.com`
- `ns-903.awsdns-48.net`
- `ns-1268.awsdns-30.org`
- `ns-1658.awsdns-15.co.uk`

The public recursive NS lookup and a direct query to the parent Cloudflare nameserver returned the same delegation set. A direct SOA query to a Route 53 authoritative server confirmed that the child zone responds. Preserve this existing zone during bootstrap. The current root session identity is recorded for review when preparing the operator IAM access described in this spec. These read-only checks verify AWS access and DNS delegation, with application deployment acceptance still tracked separately below.

## Acceptance criteria and verification

| ID | Expected result | Evidence |
| --- | --- | --- |
| D-01 | The published Production image serves Angular and the existing API under one origin. | Container test with migrated disposable PostgreSQL, real browser smoke, correct static/API routing and cache behavior |
| D-02 | HTTPS, forwarded headers, and internal probes work through the intended proxy chain. | Production proxy tests, untrusted-header case, public redirect/certificate checks, healthy ALB target |
| D-03 | RDS is private, TLS is verified, and migration/runtime roles enforce their intended privileges. | Terraform plan/configuration review and real database connectivity/permission checks |
| D-04 | Exact release SQL runs through CodeBuild before application replacement. | Commit/artifact identity checks and a successful migration result tied to the release |
| D-05 | Failed migrations block deployment and retry preserves applied history. | Controlled orchestration failure test with disposable resources, existing checksum tests, rerun verification |
| D-06 | Each eligible main push releases after successful checks with serialized environment mutation. | Observed GitHub Actions runs, including two closely spaced authorized pushes and rerun handling |
| D-07 | PR checks receive no AWS deployment credentials. | Workflow conditions, permissions, and OIDC trust-policy verification |
| D-08 | Runtime secrets are obtained from SecureString references and Data Protection survives replacement. | Redacted configuration/state review, credential access checks, synthetic key-persistence test |
| D-09 | Terraform state is encrypted, versioned, locked, and subsequent plans preserve release ownership. | Bootstrap verification and post-deployment plan review |
| D-10 | CloudWatch logs are useful, redacted, and retained for seven days. RDS automated backups remain disabled. | Observed AWS configuration and sample diagnostic events |
| D-11 | Rollback restores a retained application version and its health while preserving database state. | Authorized rollback exercise and recorded deployed identifiers |
| D-12 | First deployment and subsequent release are reproducible from the runbook. | Recorded HTTPS URL, deployed commit, first-deploy steps, and an observed follow-up release |
| D-13 | OpenAI and SES readiness findings and remaining integration prerequisites are recorded. | Provider readiness section with explicit verified/pending results |

Use existing test infrastructure for disposable PostgreSQL and Flyway. Add meaningful tests for static serving, Production proxy behavior, build identity, and release failure handling. Test deployment scripts with controlled AWS command substitutes for ordering, migration failure, rerun, and interrupted operation handling. Exercise container behavior locally before AWS acceptance. Preserve existing 9 backend, 9 frontend, and 4 Chromium cases.

Implement repository commands for Docker build/smoke, Terraform formatting/validation/plan, migration observation, release verification, and authorized rollback, then document their actual invocation. Record local and AWS results separately. Mark this spec Implemented after the acceptance criteria are verified. When cloud inputs or authorization are pending, report the prepared implementation and outstanding online checks explicitly.

## Short implementation plan

1. Confirm foundation CI evidence and deployment inputs, then pin compatible infrastructure tooling and review the approved choices.
2. Implement static frontend serving, proxy handling, release identification, Docker packaging, and Production container tests.
3. Add Terraform bootstrap/environment roots, resource permissions, secret contracts, and database bootstrap/migration runner definitions.
4. Add release automation, serialization, failure handling, rollback workflow, and executable verification scripts.
5. Run local checks and prepare the concrete infrastructure plan and runbook for review.
6. Execute authorized bootstrap/deployment steps, verify the subsequent automatic release and rollback, and record provider readiness and acceptance evidence.

## Technical references

Reviewed on 2026-11-16. The approved infrastructure choices above are design decisions based on these capabilities.

- [Elastic Beanstalk Docker image deployment](https://docs.aws.amazon.com/elasticbeanstalk/latest/dg/single-container-docker-configuration.html)
- [Elastic Beanstalk HTTPS at the load balancer](https://docs.aws.amazon.com/elasticbeanstalk/latest/dg/configuring-https-elb.html)
- [Route 53 subdomain delegation](https://docs.aws.amazon.com/Route53/latest/DeveloperGuide/CreatingNewSubdomain.html)
- [Cloudflare outgoing subdomain delegation](https://developers.cloudflare.com/dns/manage-dns-records/how-to/subdomains-outside-cloudflare/)
- [Elastic Beanstalk Parameter Store references](https://docs.aws.amazon.com/elasticbeanstalk/latest/dg/AWSHowTo.secrets.env-vars.html)
- [Elastic Beanstalk immutable updates](https://docs.aws.amazon.com/elasticbeanstalk/latest/dg/environmentmgmt-updates-immutable.html)
- [CodeBuild VPC access and NAT requirements](https://docs.aws.amazon.com/codebuild/latest/userguide/vpc-support.html)
- [RDS PostgreSQL certificate verification](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/PostgreSQL.Concepts.General.SSL.html)
- [ASP.NET Core forwarded-header trust](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/8/forwarded-headers-unknown-proxies?view=aspnetcore-10.0)
- [AWS SSM Data Protection provider](https://github.com/aws/aws-ssm-data-protection-provider-for-aspnet/blob/main/README.md)
- [Terraform S3 state and locking](https://developer.hashicorp.com/terraform/language/backend/s3)
- [Terraform write-only values](https://developer.hashicorp.com/terraform/language/manage-sensitive-data/write-only)
- [GitHub Actions concurrency](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency)

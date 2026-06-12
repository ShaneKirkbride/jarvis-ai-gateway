# Jarvis AI Gateway Agent Operating Notes

This file captures repository findings, environment-specific operating guidance, and the production-hardening backlog for future agents and maintainers. Treat it as a companion to `README.md`, not as a replacement for formal architecture decision records, threat models, runbooks, or compliance evidence.

## Repository snapshot

- **Application type:** .NET 8 minimal ASP.NET Core API that presents an OpenAI-compatible surface for Open WebUI and routes requests to Amazon Bedrock Runtime in AWS GovCloud.
- **Primary runtime project:** `src/Jarvis.AiGateway/Jarvis.AiGateway.csproj`.
- **Entry point:** `src/Jarvis.AiGateway/Program.cs` wires logging, options, JWT bearer authentication, per-user rate limiting, Bedrock Runtime client registration, custom middleware, and `/healthz`, `/v1/models`, and `/v1/chat/completions` endpoints.
- **Deployment assets:** `Dockerfile`, `deploy/ecs-task-definition.example.json`, `deploy/iam-task-policy.example.json`, `deploy/environment.example`, and `deploy/openwebui-provider.example.md`.
- **Build helpers:** `build.sh` and `build.ps1` run restore and release build for the gateway project.
- **Current branch observed during review:** `work`.
- **Important environment limitation observed during review:** `dotnet` is not installed in this container, so restore/build/test commands cannot run here until the SDK is installed or the task is run in a .NET-enabled image.

## How to operate in this repository

1. **Read any higher-priority instructions first.** Check for `AGENTS.md` files from the repository root down to the files being changed. None were present during this review, but future changes may add them.
2. **Use ripgrep and bounded file listing.** Prefer `rg --files`, `find . -maxdepth ...`, and targeted `sed`/`nl` reads. Do not use recursive `ls -R` or `grep -R` in this environment.
3. **Preserve security intent.** This service is explicitly a private enforcement gateway for GovCloud/ITAR-aware AI traffic. Avoid changes that weaken JWT validation, model allowlists, audit logging, service-to-service authentication, or data-label policy checks.
4. **Do not add secrets.** `appsettings.json`, deployment examples, and docs must keep placeholders only. Real service keys, tenant IDs, endpoint IDs, model IDs, account IDs, and KMS IDs belong in managed configuration/secrets stores.
5. **Keep changes small and reviewable.** This project is currently compact and production-shaped but not fully decomposed. Prefer small, cohesive changes with tests over broad rewrites.
6. **Validate generated JSON and Markdown.** Deployment examples are hand-edited JSON/Markdown and should be syntax-checked after changes.
7. **Install or use a .NET SDK before code validation.** The repo targets `net8.0`, while `global.json` currently pins SDK `10.0.301` with roll-forward disabled. Resolve this mismatch before relying on local build output.

## Architecture findings

### Strengths

- The gateway has a narrow responsibility: authenticate and authorize user requests, enforce gateway policy, audit the decision, and invoke Bedrock.
- Core policy, redaction, request context, user context, audit logging, and Bedrock invocation are already behind interfaces (`IPolicyEngine`, `IContentRedactor`, `IRequestContextFactory`, `IUserContextFactory`, `IAuditLogger`, and `IBedrockChatClient`), which is a good base for SOLID design and unit testing.
- Service-to-service key validation uses fixed-time comparison, which is appropriate for shared-secret comparison.
- The Bedrock model ID is separated from the user-facing model alias, which supports least privilege, safe model substitution, and environment-specific routing.
- The gateway avoids logging full prompt/response content by default and records structured audit metadata suitable for CloudWatch ingestion.
- ITAR mode is derived from headers/metadata/data labels and is checked against model approval and approved workspace IDs.
- Docker and ECS examples exist and align with a private-subnet ECS/Fargate deployment model.

### Key risks and gaps before production

1. **No automated tests are present.** Add unit and integration tests before refactoring or deploying. Priority test coverage should include policy authorization, request context extraction, user claim parsing, redaction, service API key validation, OpenAI response mapping, and Bedrock error mapping.
2. **SDK/version mismatch may block reproducible builds.** The project targets .NET 8, but `global.json` pins SDK `10.0.301` with roll-forward disabled. Choose the supported production SDK strategy intentionally: either pin an installed .NET 8 SDK or upgrade target framework/runtime/images and package versions together.
3. **Package versions use wildcards.** `AWSSDK.BedrockRuntime` and ASP.NET package references use wildcard versions. Pin exact versions or centrally manage versions to make builds reproducible and auditable.
4. **The composition root is doing too much.** `Program.cs` contains service registration, middleware order, endpoint handlers, error handling, SSE formatting, response mapping, and Bedrock exception handling. Split endpoint logic into cohesive services or endpoint modules as the application grows.
5. **Request validation is minimal.** Add explicit validation for empty model name, missing messages, unsupported roles/content parts, temperature/top-p ranges, max token bounds, metadata size, header lengths, and malformed JSON behavior.
6. **Options are not validated at startup.** Add `ValidateOnStart()` with data annotations or custom validators for `GatewayOptions`, `JwtOptions`, model routes, rate limits, service key requirements, and production-only constraints.
7. **Policy rules are hard-coded and regex-based.** Move production policy to a controlled, versioned configuration source or policy service. Add policy decision IDs, rule versions, and deterministic denial reasons for audit and support.
8. **Redaction is regex-only.** Treat current redaction as baseline protection only. Integrate enterprise DLP/classification for regulated data and secrets, and decide separately whether content should be redacted before Bedrock invocation.
9. **Streaming is not true token streaming.** Current OpenAI-compatible streaming emits one full content chunk after Bedrock returns. Replace with Bedrock `ConverseStream` before users rely on streaming latency or cancellation semantics.
10. **Operational telemetry is incomplete.** Add metrics/traces for auth failures, policy denials, rate limiting, Bedrock latency, Bedrock errors by error code, token usage, request counts, and model alias usage. Add dashboards and alarms.
11. **Health endpoint is shallow.** Keep `/healthz` lightweight for liveness, but add a separate readiness endpoint that validates configuration and optionally dependency reachability without invoking expensive model calls.
12. **Container hardening is incomplete.** Run the runtime image as a non-root user, add image scanning/SBOM/signing, review base image patch cadence, and avoid mutable `latest` tags in ECS task definitions.
13. **No CI/CD definition is present.** Add pipeline stages for formatting, restore/build/test, static analysis, dependency vulnerability scanning, container scan, IaC validation, deployment approvals, and post-deploy smoke tests.
14. **No formal threat model or compliance evidence exists in-repo.** For GovCloud/ITAR production, document data flows, trust boundaries, IAM decisions, encryption, logging retention, incident response, and control mappings.

## Production-grade checklist

### Build and dependency management

- Pin SDK and package versions deliberately.
- Add `Directory.Build.props` for common analyzer, nullable, warning, and deterministic build settings.
- Set `TreatWarningsAsErrors=true` after the current warning baseline is clean.
- Add lock files or a central package management strategy if required by the organization.
- Run dependency, license, and vulnerability scans in CI.
- Publish immutable container tags based on Git SHA or release version, never mutable `latest` in production task definitions.

### Testing strategy

- Add a test project such as `tests/Jarvis.AiGateway.Tests`.
- Unit-test pure logic first:
  - `PolicyEngine` allow/deny cases.
  - `RequestContextFactory` header/metadata precedence and ITAR derivation.
  - `UserContextFactory` subject/email/group extraction.
  - `RegexContentRedactor` redaction counts and opt-out behavior.
  - `ServiceApiKeyMiddleware` allowed/denied paths.
- Add integration tests with `WebApplicationFactory` for `/healthz`, `/v1/models`, and `/v1/chat/completions` using mocked `IBedrockChatClient` and test authentication.
- Add contract tests for OpenAI-compatible request/response shapes expected by Open WebUI.
- Add negative tests for malformed JSON, missing auth, missing service key, unknown model alias, placeholder Bedrock model IDs, and disallowed ITAR routing.
- Add load and soak tests before production to validate rate limiting, memory pressure, latency, and Bedrock throttling behavior.

### SOLID and OOP cleanliness

- **Single Responsibility Principle:** Keep endpoint handlers thin. Move chat orchestration, audit event creation, OpenAI response mapping, SSE writing, and exception-to-response mapping into dedicated classes.
- **Open/Closed Principle:** Represent policy checks as composable rules implementing a common interface so new controls can be added without editing one large policy method.
- **Liskov Substitution Principle:** Keep service interfaces behaviorally precise. For example, `IBedrockChatClient` implementations should document and consistently handle validation errors, cancellation, throttling, and provider exceptions.
- **Interface Segregation Principle:** Avoid expanding one gateway service interface into unrelated methods. Keep separate abstractions for model catalog, policy evaluation, chat completion, audit writing, and content classification.
- **Dependency Inversion Principle:** Endpoint/application orchestration should depend on abstractions, not AWS SDK concrete details. Keep AWS-specific translation inside infrastructure adapters.
- Prefer immutable request/result records where feasible for domain decisions, and mutable DTO classes only where serialization/model binding requires them.
- Introduce domain vocabulary intentionally: model route, policy decision, request context, user context, classification result, completion request, completion result, and audit event.
- Avoid static helper sprawl. Static methods in `Program.cs` should move into injectable formatters/mappers when behavior needs tests or configuration.

### Suggested refactoring roadmap

1. **Add tests before structural changes.** Characterize existing behavior and lock down security-sensitive denials.
2. **Create endpoint modules.** Move model and chat endpoint mapping into extension methods such as `MapModelEndpoints()` and `MapChatCompletionEndpoints()`.
3. **Extract chat orchestration.** Create an application service that performs user/context extraction, policy evaluation, audit population, provider invocation, and response shaping.
4. **Extract OpenAI adapters.** Create mappers for OpenAI DTOs to provider-neutral domain requests and provider-neutral results back to OpenAI responses/SSE chunks.
5. **Extract policy rules.** Convert `PolicyEngine` into a policy pipeline composed of model existence, configured model ID, group membership, prompt size, blocked pattern, and ITAR rules.
6. **Add options validation.** Validate all model routes, JWT settings, service-key settings, rate limits, and production environment invariants at startup.
7. **Add provider abstraction boundaries.** Keep Bedrock-specific request creation in a Bedrock adapter and make future providers/extensions independent of endpoint code.

### Security and compliance hardening

- Require HTTPS metadata and valid issuer/audience in production.
- Validate service API key presence at startup when `RequireServiceApiKey=true`.
- Consider mTLS or private authenticated ingress between Open WebUI/reverse proxy and the gateway.
- Enforce maximum request body size and metadata/header length limits.
- Ensure auth failures, service-key failures, policy denials, rate-limit denials, and Bedrock errors are auditable without leaking secrets or prompt text.
- Review group-claim mapping for the exact IdP being used; Entra ID group overage claims may require Graph lookup or app roles rather than raw group lists.
- Store real secrets in AWS Secrets Manager or equivalent, encrypted with KMS and constrained by IAM and VPC endpoint conditions.
- Use least-privilege Bedrock IAM resources for approved model IDs/inference profiles only.
- Add WAF/ALB protections if exposed beyond a trusted private network path.
- Define retention, access controls, and redaction policies for CloudWatch Logs.

### Observability and operations

- Add structured logging fields directly rather than serializing an audit event into a string property when downstream queryability matters.
- Add OpenTelemetry traces and metrics with correlation ID propagation.
- Emit counters/histograms for request count, latency, token usage, Bedrock latency, throttles, errors, denials, and visible model counts.
- Add CloudWatch metric filters and alarms for authentication failures, authorization denials, 429s, 5xx responses, Bedrock throttles, and high latency.
- Document operational runbooks for deployment, rollback, Bedrock outage, bad policy rollout, secret rotation, and suspicious prompt activity.
- Add a readiness endpoint separate from liveness and decide whether it checks configuration only or also verifies private endpoint/DNS reachability.

### Deployment hardening

- Use immutable ECR image tags and deployment metadata.
- Run containers as non-root and set a read-only filesystem where possible.
- Configure ECS task CPU/memory from measured load tests, not guesses.
- Use private subnets with no public IPs and required VPC endpoints for Bedrock Runtime, CloudWatch Logs/Monitoring, Secrets Manager, KMS, ECR, S3, and STS where applicable.
- Pin IAM policies to exact approved resources and conditions.
- Add blue/green or canary deployment with automatic rollback on health/metric alarms.
- Add post-deploy smoke tests that verify health, auth rejection, model listing, policy denial, and one approved completion path in a controlled environment.

## Definition of done for future code changes

- Requirements and security impact are documented in the PR.
- Unit and/or integration tests cover the changed behavior.
- `dotnet restore`, `dotnet build -c Release`, and `dotnet test` pass in a .NET-enabled environment.
- Formatting/analyzers pass, or any deviations are documented.
- No real secrets, account IDs, tenant IDs, endpoint IDs, or regulated content are committed.
- Deployment examples remain syntactically valid.
- Audit behavior is preserved or explicitly improved.
- SOLID/OOP boundaries are improved or at least not degraded.

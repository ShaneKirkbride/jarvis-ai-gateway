# Jarvis AI Gateway Agent Operating Notes

This file gives future Codex/agent runs the repo-specific context needed to work safely in this project. Treat it as the operating guide for this repository. It is a companion to `README.md`, deployment runbooks, threat models, and compliance evidence, not a replacement for them.

## Current repository snapshot

- **Purpose:** Private AI Gateway for Open WebUI to Amazon Bedrock Runtime in AWS GovCloud.
- **Portal assumption:** Open WebUI remains the user-facing portal. This gateway is the backend enforcement path for model traffic.
- **Primary project:** `src/Jarvis.AiGateway/Jarvis.AiGateway.csproj`.
- **Current project target:** `net8.0` in the checked-in `.csproj`.
- **Current Docker base images:** `.NET 8` SDK/runtime images in `Dockerfile`.
- **Current Codex environment from setup log:** `.NET SDK 10.0.301`, host/runtime `10.0.9`, Ubuntu 24.04, SDK installed under `/root/.dotnet`.
- **Important mismatch:** The environment is now .NET 10, but the repository still targets .NET 8. Do not ignore this. Either migrate the repo cleanly to .NET 10 or change the environment/global.json strategy to intentionally support .NET 8.
- **Main endpoints:** `/healthz`, `/v1/models`, and `/v1/chat/completions`.
- **Main services/interfaces:** `IPolicyEngine`, `IContentRedactor`, `IUserContextFactory`, `IRequestContextFactory`, `IBedrockChatClient`, and `IAuditLogger`.
- **Deployment examples:** `deploy/ecs-task-definition.example.json`, `deploy/iam-task-policy.example.json`, `deploy/environment.example`, and `deploy/openwebui-provider.example.md`.

## Environment rules for Codex/agents

1. **Start by checking the SDK and target framework.** Run:

   ```bash
   dotnet --info
   dotnet --list-sdks
   cat global.json 2>/dev/null || true
   cat src/Jarvis.AiGateway/Jarvis.AiGateway.csproj
   ```

2. **Resolve the .NET version strategy before code work.**
   - If this repo is intended to be .NET 10, update `TargetFramework` to `net10.0`, update Docker base images to `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/aspnet:10.0`, update ASP.NET package references to compatible pinned 10.x versions, and commit a matching `global.json`.
   - If this repo is intended to stay .NET 8, install/pin a .NET 8 SDK and keep the Dockerfile/package line on .NET 8.
   - Do not leave `global.json` pinned to .NET 10 with `rollForward: disable` while the repo and container build use .NET 8 unless that is a deliberate temporary state documented in the PR.

3. **Use targeted file discovery.** Prefer `rg --files`, `rg "text"`, `find . -maxdepth 3 -type f`, and bounded `sed -n` reads. Avoid noisy recursive commands like `ls -R` or `grep -R`.

4. **Never commit secrets.** Keep tenant IDs, endpoint IDs, account IDs, Bedrock model IDs, service API keys, KMS IDs, and PrivateLink endpoint IDs as placeholders unless the repo is explicitly approved for that data. Real values belong in AWS Secrets Manager, SSM Parameter Store, CI/CD secrets, or ECS task configuration.

5. **Preserve the security intent.** Do not weaken JWT validation, model allowlists, ITAR route checks, audit logging, service-to-service authentication, or Bedrock PrivateLink behavior for convenience.

6. **Keep changes small and reviewable.** This project is intentionally thin. Avoid turning the gateway into a monolith with UI, persistence, RAG ingestion, admin workflows, and provider orchestration all mixed into endpoint handlers.

7. **Run validation after meaningful changes.** Minimum expected checks in a .NET-enabled environment:

   ```bash
   dotnet restore src/Jarvis.AiGateway/Jarvis.AiGateway.csproj
   dotnet build src/Jarvis.AiGateway/Jarvis.AiGateway.csproj -c Release
   dotnet test || true
   docker build -t jarvis-ai-gateway .
   ```

   If tests do not exist yet, say so explicitly and add them as part of production hardening.

## Architecture findings

### What is already good

- The gateway has a clear job: validate identity, enforce policy, log the decision, and invoke Bedrock.
- The gateway uses an OpenAI-compatible surface so Open WebUI can treat it as a model provider.
- Bedrock model IDs are separated from user-facing aliases, which supports safe model substitution and least-privilege IAM.
- JWT bearer authentication is configured to validate issuer, audience, lifetime, and signing keys.
- User token forwarding via `X-Jarvis-User-Token` is supported for the stronger pattern where Open WebUI or an identity-aware proxy passes an IdP-issued token to the gateway.
- Service-to-service authentication exists through `X-Jarvis-Gateway-Key` and uses fixed-time comparison.
- Policy checks include enabled model lookup, placeholder Bedrock model ID rejection, group membership, max prompt size, blocked regex patterns, ITAR-approved model routing, and ITAR workspace checks.
- Audit logging records metadata such as user identity, groups, workspace, data label, requested alias, resolved model ID, allow/deny/error decision, token counts, latency, and endpoint mode.
- The Bedrock Runtime client supports endpoint-specific DNS override, which is important for GovCloud environments where enterprise DNS bypasses PrivateLink private DNS behavior.

### High-priority issues before production

1. **Version mismatch:** The Codex environment is .NET 10, but the repo targets .NET 8 and the Dockerfile builds/runs .NET 8. Pick one and align SDK, target framework, Docker images, package references, and global.json.
2. **Wildcard package versions:** Package references use versions such as `4.*` and `8.*`. Production builds should pin exact package versions or use central package management.
3. **No tests:** Add unit tests and integration tests before refactoring or deploying.
4. **Large composition root:** `Program.cs` currently wires services, middleware, endpoint logic, response mapping, SSE formatting, exception handling, and audit event creation. This is acceptable for a starter but should be decomposed before the codebase grows.
5. **Streaming is not true token streaming:** The OpenAI-compatible streaming path emits one full response chunk after Bedrock returns. Replace with Bedrock streaming before users depend on streaming latency/cancellation.
6. **Regex-only redaction:** Current redaction is useful as a baseline but not enough for regulated production. Integrate enterprise DLP/classification/secrets scanning.
7. **Options are not validated at startup:** Add `ValidateOnStart()` and custom validators for JWT settings, service key requirements, model routes, ITAR workspace requirements, rate limits, and production invariants.
8. **Request validation is thin:** Validate missing/empty model, missing messages, unsupported roles, unsupported content parts, temperature/top-p bounds, token limits, metadata/header size, and malformed request behavior.
9. **Container hardening is incomplete:** Runtime container should run as non-root. Use immutable tags, scanning, SBOM/signing, and pinned base images.
10. **ECS health check uses `curl`:** The sample task definition runs `curl`, but the ASP.NET runtime image may not include it. Either install curl in the image, switch to an app-native/probe-safe health command, or rely on load balancer health checks where appropriate.
11. **`latest` tags in deployment examples:** The ECS example uses `:latest`. Use immutable Git SHA or release tags for production.
12. **No CI/CD:** Add build/test/analyzer/container scan/IaC validation/deploy stages.
13. **No formal threat model:** Add trust boundaries, data flows, STRIDE-style threats, IAM assumptions, endpoint policy assumptions, logging retention, and incident response notes.

## Production-grade roadmap

### Phase 1: Make it buildable and reproducible

- Decide `.NET 10` vs `.NET 8` and align everything.
- Add or update `global.json` in the repository root.
- Pin NuGet versions.
- Add `Directory.Build.props` with nullable, analyzers, deterministic build settings, and warning policy.
- Add `Directory.Packages.props` if using central package management.
- Add `.editorconfig` and format/analyzer rules.
- Add basic CI to run restore, build, and tests.

### Phase 2: Add tests before refactoring

Create `tests/Jarvis.AiGateway.Tests`.

Unit-test at minimum:

- `PolicyEngine` allow/deny matrix.
- ITAR requests against non-ITAR model aliases.
- ITAR requests from unapproved workspaces.
- Placeholder Bedrock model ID rejection.
- Group membership extraction from different claim names.
- `RequestContextFactory` header vs metadata precedence.
- Redaction patterns and counts.
- Service API key middleware success/failure paths.
- OpenAI DTO parsing for string and array content.

Integration-test at minimum:

- `/healthz` liveness.
- `/v1/models` requires auth and filters models by group.
- `/v1/chat/completions` denies unknown model alias.
- `/v1/chat/completions` denies disallowed ITAR path.
- `/v1/chat/completions` returns OpenAI-compatible response with mocked `IBedrockChatClient`.
- Auth failure, policy denial, rate limit, and Bedrock error response shapes.

### Phase 3: Refactor while keeping SOLID/OOP clean

Keep the gateway layered:

```text
HTTP endpoints
  -> Application orchestration services
  -> Domain policy/model abstractions
  -> Infrastructure adapters such as Bedrock, logging, DLP, and config
```

Recommended refactors:

- Move endpoint registration to `Endpoints/ModelEndpoints.cs` and `Endpoints/ChatCompletionEndpoints.cs`.
- Move chat request orchestration to an application service such as `IChatCompletionGateway`.
- Move audit event construction to `IAuditEventFactory`.
- Move OpenAI response mapping and SSE formatting to `IOpenAiResponseMapper` and `IOpenAiStreamWriter`.
- Move Bedrock-specific request/response translation into `BedrockConverseChatClient` or dedicated mapper classes.
- Convert `PolicyEngine` into a composable rule pipeline:
  - `ModelExistsRule`
  - `ModelConfiguredRule`
  - `GroupAuthorizationRule`
  - `PromptSizeRule`
  - `BlockedPatternRule`
  - `ItarModelRule`
  - `ItarWorkspaceRule`
- Keep provider-specific code out of endpoint handlers.
- Prefer immutable records for domain decisions and mutable DTOs only where model binding/serialization needs them.

SOLID expectations:

- **Single Responsibility:** classes should do one job and have one reason to change.
- **Open/Closed:** add new policy checks as new rules, not by continually expanding one giant method.
- **Liskov Substitution:** interface implementations must preserve expected behavior for cancellation, errors, and policy outcomes.
- **Interface Segregation:** do not create one huge gateway service interface; keep model catalog, policy, completion, audit, classification, and provider access separate.
- **Dependency Inversion:** application logic depends on interfaces; AWS SDK specifics stay in infrastructure adapters.

### Phase 4: Security/compliance hardening

- Require service-to-service authentication in all non-dev environments.
- Consider mTLS between Open WebUI/reverse proxy and the gateway.
- Validate the user token at the gateway. Do not trust arbitrary user headers unless they are produced by a trusted identity-aware proxy or signed internal token broker.
- Add production startup validation that fails closed when JWT, model route, service key, logging, or ITAR configuration is invalid.
- Use AWS Secrets Manager or equivalent for service keys and sensitive runtime values.
- Use least-privilege IAM for Bedrock model IDs or inference profiles only.
- Keep Bedrock Runtime, CloudWatch Logs, Secrets Manager, KMS, ECR, and other required AWS APIs on VPC endpoints where supported.
- Keep S3 access through gateway or interface endpoints as required by the architecture.
- Use endpoint policies, IAM conditions such as source VPC/source endpoint where supported, and KMS key policies tied to task roles.
- Add enterprise DLP/classification for PII, credentials, controlled content, and policy-driven prompt/response handling.
- Define clear behavior for redaction before logging vs redaction before Bedrock invocation.
- Ensure audit logging captures enough metadata for review without writing full controlled prompt/response content by default.

### Phase 5: Observability and operations

- Add OpenTelemetry traces and metrics.
- Emit counters/histograms for:
  - request count
  - latency
  - Bedrock latency
  - token usage
  - visible model count
  - policy denials
  - auth failures
  - service-key failures
  - rate-limit denials
  - Bedrock throttles/errors
  - 4xx/5xx by route
- Add CloudWatch dashboards and alarms.
- Add separate `/readyz` readiness endpoint for configuration/dependency readiness.
- Add runbooks for:
  - deployment
  - rollback
  - secret rotation
  - Bedrock outage
  - PrivateLink/DNS failure
  - policy misconfiguration
  - suspicious prompt activity
  - high error rate or latency

### Phase 6: Deployment hardening

- Use immutable ECR image tags.
- Replace `latest` in ECS task definitions.
- Run as non-root.
- Consider read-only filesystem and limited Linux capabilities.
- Add image scanning, SBOM generation, and signing.
- Configure CPU/memory from load testing.
- Use private subnets, no public IPs, internal ALB/NLB only, and VPC endpoints for required AWS services.
- Add blue/green or canary deployment with automatic rollback.
- Add post-deploy smoke tests:
  - health check
  - auth rejection
  - service-key rejection
  - model list for allowed group
  - denied ITAR path
  - allowed non-ITAR path
  - one controlled Bedrock completion

## GovCloud/ITAR operating notes

- The prior GovCloud proof established the intended deployment pattern: private ECS Fargate, no public IP, internal load balancer, task role authentication, and Bedrock Runtime through a PrivateLink endpoint override when enterprise DNS resolved Bedrock publicly.
- Preserve the endpoint-specific Bedrock Runtime DNS override option. It exists because custom enterprise DNS can bypass AmazonProvidedDNS private endpoint overrides.
- Long-term, prefer conditional DNS forwarding to the VPC resolver so normal regional service names resolve privately.
- The ITAR label must not be a user-controlled approval switch. Users may declare sensitivity, but authorization should come from IdP groups, approved workspace metadata, model allowlists, and gateway policy.
- Open WebUI is not the final trust boundary. The gateway is the model-execution policy boundary.

## Definition of done for future changes

A change is not done until:

- The target .NET version strategy is still coherent.
- Restore/build/test pass in a .NET-enabled environment.
- New or changed security-sensitive behavior has tests.
- No secrets or real regulated data are committed.
- Configuration examples still use placeholders.
- JSON and Markdown examples remain valid.
- Audit behavior is preserved or intentionally improved.
- Policy failures fail closed.
- Open WebUI compatibility is preserved or the breaking change is documented.
- SOLID/OOP boundaries are improved or at least not degraded.

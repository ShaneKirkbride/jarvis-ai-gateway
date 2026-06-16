# Jarvis AI Gateway — Team Claude Instructions

This file is committed to Git. It applies to every Claude Code session in this repository.

## Project overview

Jarvis AI Gateway is a thin .NET 10 ASP.NET Core service that bridges Open WebUI to Amazon Bedrock Runtime in AWS GovCloud. It is intentionally small: validate identity, enforce policy, emit an audit event, invoke Bedrock, return an OpenAI-compatible response.

The gateway is the model-execution policy boundary for the organization. Open WebUI is the portal; this gateway is where authorization, ITAR routing, redaction, rate limiting, and audit logging happen.

**Repo layout:**

```
src/Jarvis.AiGateway/       — main ASP.NET Core project (net10.0)
tests/Jarvis.AiGateway.Tests/ — xunit tests (Moq, coverlet)
deploy/                     — ECS task definition, IAM policy, and environment examples
docs/                       — threat model, ITAR checklist, runbook, IAM matrix, data flow
Dockerfile                  — multi-stage .NET 10 build → aspnet:10.0 runtime (non-root)
global.json                 — SDK pinned to 10.0.301, rollForward disabled
build.sh / build.ps1        — CI-friendly restore + Release build scripts
```

## Tech stack

| Layer | Technology |
|---|---|
| Runtime | .NET 10 / ASP.NET Core (net10.0) |
| Auth | JWT Bearer (Azure AD GovCloud / MSAL), X-Jarvis-Gateway-Key service key |
| AI provider | Amazon Bedrock Runtime (Converse + InvokeModel fallback) |
| AWS SDK | AWSSDK.Bedrock 4.0.x, AWSSDK.BedrockRuntime 4.0.x |
| Tests | xunit 2.9, Moq 4.20, coverlet (100% line threshold on tested code) |
| Container | mcr.microsoft.com/dotnet/aspnet:10.0, non-root UID 64198 |
| Deployment | ECS Fargate, AWS GovCloud (us-gov-west-1), VPC endpoints, Secrets Manager |
| Logging | Structured JSON stdout → CloudWatch Logs |
| Health | /healthz (liveness), /readyz (readiness / config validation) |

## Commands

```bash
# Restore
dotnet restore src/Jarvis.AiGateway/Jarvis.AiGateway.csproj

# Build (Release)
dotnet build src/Jarvis.AiGateway/Jarvis.AiGateway.csproj -c Release
# or
./build.sh

# Test (100% line coverage on included files)
dotnet test

# Run locally
dotnet run --project src/Jarvis.AiGateway/Jarvis.AiGateway.csproj

# Docker build
docker build -t jarvis-ai-gateway .

# Docker run (dev, ITAR workspace check relaxed)
docker run --rm -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e Gateway__RequireItarWorkspaceForItarRequests=false \
  jarvis-ai-gateway

# Container publish (daemonless fallback — no Docker required)
dotnet publish src/Jarvis.AiGateway/Jarvis.AiGateway.csproj \
  -c Release --os linux --arch x64 \
  /t:PublishContainer \
  -p:ContainerArchiveOutputPath=./artifacts/jarvis-ai-gateway.tar.gz
```

## Repo conventions

- Target framework is `net10.0`; SDK is pinned at `10.0.301` in `global.json` with `rollForward: disable`.
- Nullable reference types are enabled; treat warnings seriously even though `TreatWarningsAsErrors=false` currently.
- Keep the gateway layered: HTTP endpoints → application services → domain policy → infrastructure adapters.
- Do not mix endpoint handler code with Bedrock-specific payload logic.
- Use dependency injection for all services; do not call `new` on service-layer dependencies inside handlers.
- Use `async`/`await` correctly — no `.Result` or `.Wait()` on async calls.
- Use structured logging (`ILogger<T>`) everywhere. Never use `Console.WriteLine` in production code.
- Keep configuration externalized. No hardcoded region names, model IDs, tenant IDs, or endpoint URIs.
- Prefer records for immutable domain decisions; mutable classes only where binding/serialization requires it.
- `Program.cs` is already large — any new endpoint or service registration should prompt a refactoring note.

## Branching and Git safety

- **Do not push directly to `main` unless explicitly instructed.** Create a feature branch and open a PR.
- Prefer small, reviewable PRs. One logical change per PR.
- Before committing, show a summary of changed files and a suggested commit message.
- Do not amend published commits on shared branches.
- Do not force-push unless explicitly asked, and never force-push `main`.
- Do not delete files without explaining the reason in the PR or commit message.

## Security rules

- **Never expose secrets, tokens, private keys, connection strings, or credentials.** This includes in code, comments, logs, test fixtures, appsettings files, or git history.
- Real values for `ServiceApiKey`, tenant IDs, Bedrock model IDs, and VPC endpoint IDs belong in AWS Secrets Manager, SSM Parameter Store, or CI/CD secrets — never committed.
- Configuration examples must use obvious placeholders (`REPLACE_WITH_*`, `<tenant-id>`, etc.).
- Do not weaken JWT validation (issuer, audience, lifetime, signing keys).
- Do not relax model allowlists, ITAR route checks, service-to-service authentication, or audit logging without a documented, reviewed reason.
- Do not log full prompt or response content. Log metadata, redaction counts, and decision outcomes only.
- Before modifying authentication, authorization, networking, deployment, or infrastructure code, explain the risk and proposed change first and wait for acknowledgment.
- Do not introduce SSRF vulnerabilities — all outbound calls must go to configured, trusted endpoints (Bedrock Runtime, CloudWatch, Secrets Manager via VPC endpoints).
- Validate all inputs at HTTP boundary entry points. Reject unsupported content types, malformed models, and out-of-bounds parameters with clear OpenAI-compatible error responses.

## DevOps and deployment notes

- Deployment target: ECS Fargate, private subnets, no public IP, internal ALB, GovCloud (`us-gov-west-1`).
- Container runs as non-root UID 64198. Do not change `USER jarvis` in the Dockerfile without review.
- Use immutable ECR image tags (Git SHA or release tag). Do not deploy `:latest` to production.
- Bedrock Runtime is reached through a VPC endpoint DNS override when enterprise DNS bypasses private hosted zone behavior.
- ECS uses two IAM roles: execution role (ECR pulls + CloudWatch log delivery) and task role (Bedrock Runtime + Secrets Manager + KMS).
- Do not bake AWS credentials into the image. Use the ECS task role.
- Required VPC endpoints: Bedrock Runtime, CloudWatch Logs, CloudWatch Monitoring, Secrets Manager, KMS, ECR API, ECR DKR, S3 gateway/interface.
- Health checks: use ALB target health checks against `/healthz`. Use `/readyz` for deployment smoke tests.
- CI/CD pipeline should run: restore → build → test → analyzer → container build → image scan → deploy to staging → smoke test → promote.

## Compliance notes (ITAR / GovCloud)

- This gateway enforces ITAR data routing. ITAR approval is determined by IdP groups, approved workspace IDs, and model `ItarApproved` flags — **not** by user-supplied headers alone.
- `X-Jarvis-Data-Label: ITAR_APPROVED` is a user-declared hint; gateway policy is the authoritative enforcement point.
- Deny outcomes must fail closed. A misconfigured policy must result in denial, not silent permit.
- ITAR workspace IDs are configured server-side in `Gateway:ItarApprovedWorkspaceIds`. Do not accept arbitrary workspace IDs as ITAR-approved without that config.
- Audit logs must capture enough metadata for compliance review: user subject, groups, workspace, data label, model alias, resolved Bedrock model ID, allow/deny decision, token counts, latency.
- See `docs/itar-approval-checklist.md`, `docs/threat-model.md`, and `docs/iam-matrix.md` for compliance reference material.

## Definition of done

A change is not ready to commit until:
- Restore, build, and tests pass locally.
- New or changed security-sensitive behavior has tests.
- No secrets or real regulated data are committed.
- Configuration examples use placeholders.
- Audit behavior is preserved or intentionally improved.
- Policy failures still fail closed.
- Open WebUI API compatibility is preserved or the breaking change is documented.

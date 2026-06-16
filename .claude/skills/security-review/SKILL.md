---
description: Auto-invoked security review for Jarvis AI Gateway. Activates when work touches authentication, authorization, secrets, tokens, API keys, network boundaries, deployment config, infrastructure, logging, data storage, AI gateway/model routing, or user-provided input.
triggers:
  - authentication
  - authorization
  - jwt
  - api key
  - service key
  - token
  - secret
  - credential
  - bedrock
  - model routing
  - itar
  - redaction
  - audit log
  - logging
  - middleware
  - network
  - vpc
  - endpoint
  - ecs
  - iam
  - docker
  - deploy
  - user input
  - prompt
---

# Security Review Skill — Jarvis AI Gateway

This skill activates automatically when a change touches any security-sensitive area of the gateway. Run through all applicable checks before reporting the change as complete.

## Activation triggers

Run this review when work touches any of:
- `Security/` — `ServiceApiKeyMiddleware.cs` or similar
- `Program.cs` — auth pipeline, middleware order, JWT configuration
- `Services/PolicyEngine.cs` or `IPolicyEngine`
- `Services/ContentRedactor.cs` or `IContentRedactor`
- `Services/AuditLogger.cs` or `IAuditLogger`
- `Services/UserContextFactory.cs` or `IUserContextFactory`
- `Options/GatewayOptions.cs` — any security-related option
- `appsettings.json` — JWT, service key, ITAR, policy settings
- `deploy/` — ECS task definition, IAM policy, environment config
- `Dockerfile` — user, base image, exposed ports
- Any file touching model allowlists, ITAR routing, or group-based authorization
- Any file that processes user-provided prompt or message content

## Checklist

### Secret and credential exposure

- [ ] No API keys, service keys, JWT secrets, or AWS credentials in source code, test fixtures, or config files.
- [ ] No real tenant IDs, Bedrock model IDs, VPC endpoint IDs, or account IDs committed.
- [ ] `appsettings.json` and `appsettings.Development.json` use only placeholders for sensitive values.
- [ ] Logs do not emit `ServiceApiKey`, JWT token values, full prompt content, or response content.
- [ ] `AuditLogger` emits metadata only — not raw prompt/response text.

### Authentication

- [ ] JWT validation is not weakened: issuer, audience, token lifetime, and signing key validation remain active.
- [ ] `Jwt:RequireHttpsMetadata` is not disabled in production.
- [ ] Service API key check uses fixed-time comparison (no timing side channel).
- [ ] `X-Jarvis-User-Token` forwarding does not bypass JWT validation — it supplements it.
- [ ] Middleware order is correct: auth middleware runs before policy/endpoint handlers.

### Authorization and policy

- [ ] Policy failures fail closed. An exception during a policy check results in denial, not permit.
- [ ] ITAR routing depends on server-side configuration (`ItarApprovedWorkspaceIds`, `ItarApproved` model flags, `RequiredGroups`) — not on user-supplied headers alone.
- [ ] Group membership is extracted from validated JWT claims, not from request headers.
- [ ] Model allowlist is enforced. Unknown or placeholder model IDs (`REPLACE_WITH_*`) are rejected.
- [ ] `stream=true` is rejected unless explicitly enabled via `Gateway:Streaming:FallbackToNonStreaming`.

### Unsafe logging

- [ ] No prompt content, response content, or PII logged at `Information` or higher by default.
- [ ] Redaction runs before logging when `Gateway:Redaction:RedactBeforeLogging=true`.
- [ ] No exception handlers that log full request bodies.
- [ ] Correlation IDs in logs do not carry user-controlled data without validation.

### SSRF and injection risks

- [ ] All outbound HTTP calls target configured, trusted endpoints (Bedrock Runtime, CloudWatch, Secrets Manager via VPC endpoints).
- [ ] No user-controlled URL construction in Bedrock client or AWS SDK calls.
- [ ] Bedrock endpoint DNS override is from trusted configuration (`Gateway:BedrockRuntimeEndpointDns`), not from a request header.
- [ ] Regex patterns used in `BlockedPromptPatterns` have explicit timeouts (`RegexTimeoutMilliseconds`) to prevent ReDoS.

### Input validation

- [ ] Request body size is bounded by `Gateway:MaxRequestBodyBytes`.
- [ ] Model alias is validated before use.
- [ ] Message content is validated to text-only; non-text content types are rejected with `400`.
- [ ] Temperature, token limits, stop sequences, and metadata size are validated.
- [ ] Unsupported roles and malformed message shapes are rejected.

### IAM and least privilege

- [ ] ECS task role grants Bedrock access only to specific model ARNs or inference profiles — not `*`.
- [ ] Task role does not have `bedrock:ListFoundationModels` unless model discovery is deliberately enabled.
- [ ] No broad `*` actions in the task policy without documented justification.
- [ ] Execution role is separate from task role.

### Container and deployment

- [ ] Container runs as non-root (`USER jarvis`, UID 64198).
- [ ] No secrets in `Dockerfile` `ENV`, `ARG`, or `COPY` instructions.
- [ ] Image tag in ECS task definition is an immutable SHA or release tag — not `:latest`.
- [ ] `ASPNETCORE_URLS` does not expose the app on public interfaces in production.

### Data retention and compliance

- [ ] Audit logs capture enough metadata for ITAR compliance review without writing controlled content.
- [ ] Log retention in CloudWatch is configured and not indefinite unless required.
- [ ] ITAR-labeled requests are routed only to `ItarApproved=true` models.
- [ ] Policy denial reasons use stable, documented rule IDs (`ITAR_MODEL_DENIED`, `ITAR_WORKSPACE_DENIED`, etc.).

## Report format

For each failed check, state:
- File and approximate location
- What the risk is
- Exploitability in the GovCloud/ECS deployment context
- Recommended remediation

Flag `CRITICAL` (exploitable now), `HIGH` (likely exploitable), `MEDIUM` (defense-in-depth concern), or `LOW` (hardening recommendation).

# Jarvis AI Gateway

A thin .NET 10 AI Gateway for Open WebUI → Amazon Bedrock Runtime in AWS GovCloud.

This gateway is intentionally small. It gives Open WebUI an OpenAI-compatible API surface while keeping the real security controls in a private backend service.

## What it does

- Exposes OpenAI-compatible endpoints:
  - `GET /v1/models`
  - `POST /v1/chat/completions`
- **In-gateway identity broker (default)**: accepts Open WebUI's native user-info headers
  (`X-OpenWebUI-User-Email`, etc.) as the identity assertion, resolves AD groups via
  Microsoft Graph, and authorizes on Entra group object IDs.  Origin trust is provided by
  mTLS terminated at the internal ALB in verify mode.  See
  [`docs/identity-broker.md`](docs/identity-broker.md) and
  [`docs/identity-broker-plan.md`](docs/identity-broker-plan.md).
- **Legacy ASP.NET Core JwtBearer authentication path** (opt-in): set
  `Gateway:IdentityBroker:Enabled=false` to revert to the IdP-JWT validation flow used
  before the broker.  Intended for migration windows and break-glass rollback.
- Supports `X-Jarvis-User-Token` for user-token forwarding from Open WebUI or an identity-aware proxy on the legacy path.
- Optionally requires a service-to-service API key via `X-Jarvis-Gateway-Key`.
- Enforces model aliases and Bedrock model allowlists.
- Enforces ITAR mode by user group, workspace ID, and model approval status.
- Performs basic prompt policy checks and regex-based PII/secrets redaction.
- Applies per-user fixed-window rate limiting.
- Emits structured audit logs to stdout for CloudWatch ingestion.
- Invokes Amazon Bedrock Runtime using the AWS SDK for .NET and the ECS task role.
- Supports a Bedrock Runtime VPC endpoint DNS override for GovCloud PrivateLink deployments with custom DNS.

## High-level runtime path

```text
Open WebUI -> Jarvis AI Gateway -> Amazon Bedrock Runtime -> Model response
```

The gateway expects to run inside private subnets, behind an internal ALB/NLB, with no public IP and with Bedrock Runtime reached through a VPC endpoint.

## Why this exists

Open WebUI is the portal. It gives users chat, workspaces, prompt libraries, model selection, and knowledge features. This gateway is the backend enforcement point. It validates identity, checks policy, logs the decision, and only then invokes Bedrock.

## Current scope

This is a production-shaped starter, not a fully certified product. Before production use, validate it against your security, compliance, IAM, logging, and networking standards.

Implemented:

- Explicit Bedrock model aliases by default. Optional Bedrock model discovery through `ListFoundationModels` is supported but disabled in production examples.
- Policy-gated OpenAI-compatible model listing for chat/text models.
- Non-streaming Bedrock Converse calls.
- Conservative provider-specific `InvokeModel` fallback adapters for known text model families.
- OpenAI-compatible non-streaming response.
- `stream=true` is rejected by default with HTTP 400 until true Bedrock ConverseStream token streaming is implemented.
- Polly retry with exponential backoff + jitter on transient Bedrock errors (throttling, 5xx). Does not retry access-denied or client errors.
- Graceful shutdown on SIGTERM: in-flight Bedrock calls drain for up to 30 seconds before the process exits.
- OpenTelemetry metrics exported via OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Default (unset): metrics are collected but not exported. See the ADOT sidecar pattern below.
- PII/secrets redacted before sending prompts to Bedrock. ITAR requests are always redacted regardless of configuration.
- Generic client-facing policy denial messages. The specific denial reason (ITAR approval, workspace, group) is captured in the server-side audit log only.

Recommended next hardening:

- Implement Bedrock `ConverseStream` before enabling streaming in Open WebUI.
- Replace regex-only redaction with enterprise DLP/classification service calls.
- Add mTLS between Open WebUI and the gateway.
- Add persistent policy configuration backed by a controlled config source.
- Expand automated integration tests and CI/CD.
- Add CloudWatch metric filters and alarms for policy denials, auth failures, throttling, and Bedrock errors.
- Add a Polly circuit breaker on the resilience pipeline after the retry stage.

## Configuration

Main settings live under `Gateway` and `Jwt`.

Important environment variables:

```bash
Gateway__AwsRegion=us-gov-west-1
Gateway__BedrockRuntimeEndpointDns=vpce-REPLACE.bedrock-runtime.us-gov-west-1.vpce.amazonaws.com
Jwt__Authority=https://login.microsoftonline.us/<tenant-id>/v2.0
Jwt__Audience=api://jarvis-ai-gateway
Gateway__RequireServiceApiKey=true
Gateway__ServiceApiKey=<from Secrets Manager>
Gateway__ModelDiscovery__Enabled=false
Gateway__Streaming__FallbackToNonStreaming=false
```

### Identity broker (default on as of PR 3; mTLS subject pinning default on as of PR 4)

```bash
# Identity broker — default true; full setup lives in docs/identity-broker.md
Gateway__IdentityBroker__Enabled=true
Gateway__IdentityBroker__AuditSubjectSalt=<from Secrets Manager>
Gateway__IdentityBroker__OwuiTrustedHeader__Enabled=true
Gateway__IdentityBroker__OwuiTrustedHeader__RequireMtlsSubjectPinning=true
# Mtls.RequireSubjectCheck is default-on as of PR 4 — configure at least one accepted
# subject (or serial) or the gateway refuses readiness.
Gateway__IdentityBroker__Mtls__AcceptedClientCertSubjects__0=CN=jarvis-openwebui,...
Gateway__IdentityBroker__Graph__TenantId=<entra tenant id>
Gateway__IdentityBroker__Graph__ClientId=<gateway app registration id>
Gateway__IdentityBroker__Graph__ClientSecret=<from Secrets Manager>

# Opt-out (legacy JwtBearer path) — for in-flight migrations or break-glass rollback only.
# When false the gateway runs the ASP.NET Core JWT bearer pipeline as before and the
# Gateway:IdentityBroker:* keys above are ignored.
# Gateway__IdentityBroker__Enabled=false
```


Request hardening defaults are configurable under `Gateway`:

```bash
Gateway__MaxRequestBodyBytes=1048576
Gateway__ProviderTimeoutSeconds=120
Gateway__ModelDiscoveryTimeoutSeconds=10
Gateway__ReadinessTimeoutSeconds=5
Gateway__RequestLimits__MaxMetadataEntries=16
Gateway__RequestLimits__MaxMetadataKeyLength=64
Gateway__RequestLimits__MaxMetadataValueLength=512
Gateway__RequestLimits__MaxGatewayHeaderLength=512
Gateway__RequestLimits__MaxStopSequenceCount=4
Gateway__RequestLimits__MaxStopSequenceLength=200
Gateway__RequestLimits__MaxMessageCount=128
Gateway__RequestLimits__RegexTimeoutMilliseconds=250
```

`POST /v1/chat/completions` accepts text-only OpenAI message content. Unsupported image, audio, tool, arbitrary object, or mixed non-text content is rejected with an OpenAI-compatible `400` response instead of being silently converted into prompt text. Configured regex patterns are validated at startup and are evaluated with explicit timeouts.

Model routes are configured by alias:

```bash
Gateway__Models__0__Alias=jarvis-govcloud-general
Gateway__Models__0__BedrockModelId=<actual Bedrock model ID or inference profile ARN>
Gateway__Models__0__ItarApproved=false
Gateway__Models__0__AllowedGroups__0=AI-General-Users

Gateway__Models__1__Alias=jarvis-govcloud-itar
Gateway__Models__1__BedrockModelId=<actual ITAR-approved Bedrock model ID or inference profile ARN>
Gateway__Models__1__ItarApproved=true
Gateway__Models__1__AllowedGroups__0=AI-ITAR-Approved
```

## Build locally

```bash
dotnet restore src/Jarvis.AiGateway/Jarvis.AiGateway.csproj
dotnet build src/Jarvis.AiGateway/Jarvis.AiGateway.csproj -c Release
dotnet run --project src/Jarvis.AiGateway/Jarvis.AiGateway.csproj
```

## Docker build

The repository keeps a Dockerfile for real image builds in local development, GitHub Actions, AWS CodeBuild, or another runner with Docker installed and a reachable Docker daemon.

```bash
docker build -t jarvis-ai-gateway .
docker run --rm -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e Gateway__RequireItarWorkspaceForItarRequests=false \
  jarvis-ai-gateway
```

If Docker is unavailable in an ephemeral validation environment, use the .NET SDK container publish target as a daemonless fallback validation path:

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet publish src/Jarvis.AiGateway/Jarvis.AiGateway.csproj -c Release --os linux --arch x64 /t:PublishContainer -p:ContainerArchiveOutputPath=./artifacts/jarvis-ai-gateway.tar.gz
```

For local calls to Bedrock, use your normal AWS credential chain. For ECS deployment, do not bake credentials into the image. Use the ECS task role.

## Readiness

`/healthz` is a lightweight liveness endpoint. `/readyz` validates non-secret runtime readiness, including AWS region, JWT authority/audience, service-key requirements, enabled aliases, and production placeholder model IDs. The ECS example omits a container `curl` health check; prefer internal ALB target health checks against `/healthz` and deployment smoke checks against `/readyz`.

### Optional Bedrock connectivity check

Set `Gateway__Readiness__CheckBedrockConnectivity=true` to have `/readyz` attempt a live `ListFoundationModels` call as part of the readiness check. This verifies VPC endpoint DNS resolution and task-role IAM reachability before the first real invocation. Requires `bedrock:ListFoundationModels` on the task role (see `deploy/iam-task-policy.example.json`). The check uses `Gateway:ReadinessTimeoutSeconds` (default 5 s) and fails with an actionable message on timeout, access-denied, or DNS error.

This setting defaults to `false` so deployments using explicit aliases without model discovery do not need the extra IAM permission.

## Model discovery tradeoff

Production examples disable Bedrock model discovery and rely on explicit aliases. This avoids granting `bedrock:ListFoundationModels` and keeps model exposure deterministic. If discovery is deliberately enabled, add the IAM permission, keep raw Bedrock IDs hidden unless approved, and document why discovery is needed.

## Streaming behavior

Configure Open WebUI to send `stream: false`. The gateway rejects `stream=true` by default with HTTP 400 (`streaming_unsupported`). This is intentional: returning a single buffered response chunk is not true token streaming, and Open WebUI's streaming path assumes incremental tokens.

Enable streaming only after implementing Bedrock `ConverseStream` end-to-end. Set `Gateway:Streaming:FallbackToNonStreaming=true` only for local testing — do not enable in production without full streaming support.

## Smoke tests

Health check:

```bash
curl http://localhost:8080/healthz
```

List models with a user JWT:

```bash
curl -s http://localhost:8080/v1/models \
  -H "Authorization: Bearer $USER_IDP_JWT"
```

Chat completion:

```bash
curl -s http://localhost:8080/v1/chat/completions \
  -H "Authorization: Bearer $USER_IDP_JWT" \
  -H "Content-Type: application/json" \
  -H "X-Jarvis-Workspace-Id: jarvis-itar" \
  -H "X-Jarvis-Data-Label: ITAR_APPROVED" \
  -d '{
    "model":"jarvis-govcloud-itar",
    "messages":[{"role":"user","content":"Say hello from GovCloud in one sentence."}],
    "stream": false
  }'
```

If using Open WebUI behind an identity-aware proxy:

```bash
curl -s http://localhost:8080/v1/chat/completions \
  -H "X-Jarvis-Gateway-Key: $SERVICE_KEY" \
  -H "X-Jarvis-User-Token: $USER_IDP_JWT" \
  -H "Content-Type: application/json" \
  -d '{
    "model":"jarvis-govcloud-general",
    "messages":[{"role":"user","content":"Write one secure architecture sentence."}],
    "stream": false
  }'
```

## Required AWS resources

Recommended VPC endpoints:

- Bedrock Runtime interface endpoint
- CloudWatch Logs interface endpoint
- CloudWatch Monitoring interface endpoint
- Secrets Manager interface endpoint
- KMS interface endpoint
- ECR API interface endpoint
- ECR DKR interface endpoint
- S3 gateway endpoint, or S3 interface endpoint if the specific access pattern requires PrivateLink semantics
- STS interface endpoint if directly used by the app or diagnostics

## ECS IAM model

Use two separate roles:

- **Execution role** (`iam-execution-role-policy.example.json`): Assumed by the ECS agent before the container starts. Grants ECR image pulls, CloudWatch Logs delivery, and optional Secrets Manager injection at launch time. This role is NOT available inside the running container.
- **Task role** (`iam-task-policy.example.json`): Assumed by the running container. Grants Bedrock Runtime invocation, Secrets Manager reads (if the app fetches secrets at runtime), and KMS decrypt. Do not grant ECR or log delivery here.

Do not store AWS credentials in the container image. Use the task role for all runtime AWS calls.

## Security behavior

### Policy denial messages

The gateway returns a generic `"Request is not allowed by policy."` message with HTTP 403 for all policy denials, regardless of the specific rule that fired (ITAR model approval, workspace approval, group check, blocked pattern, etc.). The specific denial reason, rule ID, and internal policy state are captured in the server-side audit log only. This prevents internal policy details from leaking to clients.

### Content redaction

`Gateway:Redaction:RedactBeforeBedrock` defaults to `true`. Prompts are redacted for PII and secrets patterns before being sent to Bedrock. ITAR requests enforce redaction regardless of this setting — the gateway always redacts before forwarding ITAR-labeled or ITAR-mode content.

Set `RedactBeforeBedrock=false` in development to inspect prompts in Bedrock provider logs. Never set it to `false` in production ITAR workloads.

## Metrics and observability

The gateway uses OpenTelemetry to expose `GatewayMetrics` counters and histograms (request count, latency, token usage, policy denials, Bedrock errors). Metrics are collected at all times but only exported when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

### ADOT sidecar pattern (recommended for ECS)

Run the [AWS Distro for OpenTelemetry (ADOT) Collector](https://aws-otel.github.io/) as an ECS sidecar container. In the task definition:

1. Add the ADOT Collector container alongside the gateway container.
2. Configure the ADOT Collector to receive OTLP gRPC on port 4317 and forward to CloudWatch or another backend.
3. Set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` in the gateway container environment.

No code change is needed to switch between development (no export) and production (ADOT sidecar). See `deploy/environment.example` for the complete variable list.

## Resilience

The gateway uses a Polly resilience pipeline around every Bedrock invocation:

- Retries up to 3 times with exponential backoff + jitter (starting at 1 s) on `ThrottlingException` and 5xx Bedrock runtime errors.
- Does NOT retry: access-denied, validation errors, client cancellation, or provider timeout.
- The pipeline runs inside the per-request provider timeout (`Gateway:ProviderTimeoutSeconds`). Each retry consumes time from that budget.
- On SIGTERM, in-flight requests drain for up to 30 seconds (`HostOptions.ShutdownTimeout`) before the process exits.

## Audit logging

The gateway emits structured audit events with:

- request ID
- correlation ID
- user subject/email/groups
- workspace ID
- data label and ITAR mode
- requested model alias
- resolved Bedrock model ID
- allow/deny/error decision
- token counts
- latency
- endpoint mode

By default, the gateway does not log full prompt or response content. It logs metadata and redaction counts.

## Dynamic Bedrock model discovery

The gateway discovers foundation models with the Amazon Bedrock control-plane `ListFoundationModels` API. Discovery is performed by `IBedrockModelDiscoveryService`, cached for `Gateway:ModelDiscovery:CacheSeconds`, and filtered before the models enter the gateway registry.

Discovery preserves Bedrock metadata including model ID, model name, provider, input/output modalities, supported inference types, response streaming support, lifecycle status, and a best-effort `SupportsConverse` capability. The registry then merges that metadata with configured aliases, model policy, ITAR flags, group requirements, and known invocation capabilities.

Discovery is **not authorization**. The safe production default is:

```json
"ModelDiscovery": {
  "Enabled": true,
  "CacheSeconds": 300,
  "IncludeLifecycleStatuses": [ "ACTIVE" ],
  "IncludeOutputModalities": [ "TEXT" ],
  "ExcludeProviders": [],
  "IncludeProviders": [],
  "ExposeRawBedrockModelIds": false
},
"Policy": {
  "RequireExplicitModelAllowlist": true,
  "DeniedModelIdPatterns": [],
  "AllowedModelIdPatterns": [],
  "AllowDiscoveredModelsForNonItar": false,
  "AllowDiscoveredModelsForItar": false
}
```

With this configuration, newly discovered models can be observed by gateway operators through logs/configuration work, but they are not callable from Open WebUI unless an administrator explicitly configures an alias or policy allowlist.

## Exposing aliases to Open WebUI

Open WebUI should normally see friendly aliases rather than raw Bedrock model IDs. Configure aliases under `Gateway:Models`:

```json
"Models": [
  {
    "Alias": "jarvis-govcloud-general",
    "DisplayName": "Jarvis GovCloud General",
    "BedrockModelId": "anthropic.claude-3-haiku-20240307-v1:0",
    "Enabled": true,
    "ItarApproved": false,
    "RequiredGroups": [ "AI-General-Users" ],
    "InvocationMode": "Auto",
    "MaxInputCharacters": 120000,
    "MaxOutputTokens": 2048
  },
  {
    "Alias": "jarvis-govcloud-itar",
    "DisplayName": "Jarvis GovCloud ITAR Approved",
    "BedrockModelId": "<ITAR-approved model ID or inference profile ARN>",
    "Enabled": true,
    "ItarApproved": true,
    "RequiredGroups": [ "AI-ITAR-Approved" ],
    "InvocationMode": "Auto"
  }
]
```

`GET /v1/models` returns only allowed chat/text models in the OpenAI-compatible shape:

```json
{
  "object": "list",
  "data": [
    {
      "id": "jarvis-govcloud-general",
      "object": "model",
      "created": 0,
      "owned_by": "aws-bedrock"
    }
  ]
}
```

To expose raw Bedrock model IDs for controlled testing, set `Gateway:ModelDiscovery:ExposeRawBedrockModelIds=true` and add either `AllowedModelIdPatterns` or one of the `AllowDiscoveredModelsFor*` flags. Do not enable raw exposure broadly in production unless your governance process requires it.

## Safely allowing a discovered model

1. Confirm the model is enabled in the AWS account and region.
2. Confirm lifecycle is `ACTIVE` and output modality includes `TEXT`.
3. Confirm the model is approved for the data class. For ITAR workloads, set `ItarApproved=true` only after your compliance review.
4. Prefer adding a friendly alias in `Gateway:Models` with `RequiredGroups` instead of exposing raw IDs.
5. Keep `Policy:DeniedModelIdPatterns` populated for models or providers that must never be used.
6. Use `InvocationMode=Auto` unless you have a reason to force `Converse` or `InvokeModel`.

## Invocation strategy

The gateway selects invocation by capability:

1. **Converse first**: chat/text models known to support Bedrock Converse use `IAmazonBedrockRuntime.ConverseAsync` for a consistent message interface.
2. **InvokeModel fallback**: non-Converse text models use a provider-specific `IInvokeModelPayloadAdapter` only when the gateway has an explicit, conservative adapter.
3. **Unsupported models fail clearly**: if a discovered model has no Converse support and no adapter, the gateway returns `501` with `Model is discovered but not supported by this gateway invocation adapter yet.`

Current conservative InvokeModel adapters cover:

- Amazon Titan text (`amazon.titan-text*`)
- Meta Llama text/chat/instruct (`meta.llama*`)
- Mistral text/chat/instruct (`mistral.*`)

OpenAI GPT OSS and NVIDIA Nemotron adapter classes should be added only after confirming the exact Bedrock `InvokeModel` request/response schema for the target model IDs. Do not guess payload schemas.

To add a provider-specific adapter:

1. Implement `IInvokeModelPayloadAdapter`.
2. Make `CanHandle` match only the provider/model family with a known schema.
3. Build the exact documented JSON body in `BuildRequestBody`.
4. Parse the exact response shape in `ParseResponseBody`.
5. Register the adapter in DI in `Program.cs`.
6. Add tests for `CanHandle`, request body generation, response parsing, and unsupported model fallback.

## GovCloud and PrivateLink configuration

For AWS GovCloud West, set:

```bash
Gateway__AwsRegion=us-gov-west-1
```

For Bedrock Runtime PrivateLink endpoint override, set the runtime endpoint DNS name:

```bash
Gateway__BedrockRuntimeEndpointDns=vpce-REPLACE.bedrock-runtime.us-gov-west-1.vpce.amazonaws.com
```

If your environment also requires a Bedrock control-plane PrivateLink/custom endpoint for `ListFoundationModels`, set:

```bash
Gateway__BedrockEndpointDns=vpce-REPLACE.bedrock.us-gov-west-1.vpce.amazonaws.com
```

The gateway normalizes endpoint values by adding `https://` when omitted.

## Current limitations

- `/v1/chat/completions` supports text/chat models only.
- `/v1/embeddings` is future work; embedding models are tracked by discovery but not exposed as chat models.
- Image generation is future work; image models are not exposed through `/v1/chat/completions`.
- Not every discovered Bedrock model supports Converse.
- Non-Converse models need explicit `InvokeModel` adapter support.
- Full Bedrock streaming is not implemented. If `stream=true` is requested, the gateway falls back to non-streaming when `Gateway:Streaming:FallbackToNonStreaming=true`; otherwise it returns a clear error.

# Jarvis AI Gateway

A thin .NET 10 AI Gateway for Open WebUI → Amazon Bedrock Runtime in AWS GovCloud.

This gateway is intentionally small. It gives Open WebUI an OpenAI-compatible API surface while keeping the real security controls in a private backend service.

## What it does

- Exposes OpenAI-compatible endpoints:
  - `GET /v1/models`
  - `POST /v1/chat/completions`
- Validates an IdP-issued JWT using ASP.NET Core JWT bearer authentication.
- Supports `X-Jarvis-User-Token` for user-token forwarding from Open WebUI or an identity-aware proxy.
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

Recommended next hardening:

- Implement Bedrock `ConverseStream` before enabling streaming in Open WebUI.
- Replace regex-only redaction with enterprise DLP/classification service calls.
- Add mTLS between Open WebUI and the gateway.
- Add persistent policy configuration backed by a controlled config source.
- Expand automated integration tests and CI/CD.
- Add CloudWatch metric filters and alarms for policy denials, auth failures, throttling, and Bedrock errors.

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

## Model discovery tradeoff

Production examples disable Bedrock model discovery and rely on explicit aliases. This avoids granting `bedrock:ListFoundationModels` and keeps model exposure deterministic. If discovery is deliberately enabled, add the IAM permission, keep raw Bedrock IDs hidden unless approved, and document why discovery is needed.

## Streaming behavior

Open WebUI should send `stream: false`. The gateway rejects `stream=true` by default because returning one full response chunk is not true token streaming. Enable streaming only after implementing and testing Bedrock ConverseStream.

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

Use two roles:

- **Execution role**: ECR image pulls and CloudWatch log delivery.
- **Task role**: application access to Bedrock Runtime, Secrets Manager, KMS, and any required S3 resources.

Do not store AWS keys in the container image.

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

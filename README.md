# Jarvis AI Gateway

A thin .NET 8 AI Gateway for Open WebUI → Amazon Bedrock Runtime in AWS GovCloud.

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

- Non-streaming Bedrock Converse calls.
- OpenAI-compatible non-streaming response.
- OpenAI-compatible streaming response shape, but emitted as one full chunk after Bedrock returns. This keeps Open WebUI compatibility without implementing true Bedrock token streaming yet.

Recommended next hardening:

- Replace one-chunk streaming with Bedrock `ConverseStream`.
- Replace regex-only redaction with enterprise DLP/classification service calls.
- Add mTLS between Open WebUI and the gateway.
- Add persistent policy configuration backed by a controlled config source.
- Add automated tests and CI/CD.
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

```bash
docker build -t jarvis-ai-gateway .
docker run --rm -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e Gateway__RequireItarWorkspaceForItarRequests=false \
  jarvis-ai-gateway
```

For local calls to Bedrock, use your normal AWS credential chain. For ECS deployment, do not bake credentials into the image. Use the ECS task role.

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
    "stream": true
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

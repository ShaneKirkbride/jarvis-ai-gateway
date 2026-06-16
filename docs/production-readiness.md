# Production Readiness Guide

Jarvis AI Gateway is intended to run as the private enforcement path between Open WebUI and Amazon Bedrock Runtime in AWS GovCloud.

## Required production posture

- Run in private subnets behind an internal ALB/NLB.
- Deploy containers with immutable ECR tags or image digests, not `latest`.
- Set `ASPNETCORE_ENVIRONMENT=Production`.
- Set `Gateway__RequireServiceApiKey=true` and source `Gateway__ServiceApiKey` from Secrets Manager or an equivalent secret store.
- Keep `Gateway__ModelDiscovery__Enabled=false` unless `bedrock:ListFoundationModels` has been explicitly approved and audited.
- Configure explicit model aliases under `Gateway__Models` and approve ITAR aliases separately.
- Keep `Gateway__Streaming__FallbackToNonStreaming=false`; `stream=true` returns HTTP 400 until ConverseStream is implemented.

## Health and readiness

- `/healthz` is lightweight liveness.
- `/readyz` verifies non-secret configuration readiness: AWS region, JWT authority/audience, service-key requirement, enabled aliases, and production placeholder model IDs.
- ALB target group health checks can use `/healthz`; deployment and pre-traffic checks should call `/readyz`.

## Validation

Normal pull-request validation does not require AWS credentials. Run:

```bash
dotnet restore
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release
dotnet list package --vulnerable --include-transitive
docker build -t jarvis-ai-gateway .
```

## Deferred production items

- True Bedrock ConverseStream token streaming.
- Enterprise DLP/classification integration beyond regex redaction.
- mTLS between Open WebUI/proxy and gateway.
- Full OpenTelemetry exporter wiring and dashboards.
- Blue/green deployment automation and real cloud smoke tests.

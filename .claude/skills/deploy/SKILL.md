---
description: Auto-invoked deployment safety review for Jarvis AI Gateway. Activates when work touches Dockerfile, ECS task definition, IAM policy, environment config, build scripts, or CI/CD files.
triggers:
  - dockerfile
  - ecs
  - ecs-task-definition
  - iam
  - iam-task-policy
  - environment.example
  - deploy/
  - build.sh
  - build.ps1
  - .github/
  - ci
  - cd
  - pipeline
  - release
  - container
  - image
---

# Deployment Safety Skill — Jarvis AI Gateway

This skill activates automatically when a change touches deployment artifacts. Run through all applicable checks before reporting the change as safe to deploy.

## Activation triggers

Run this review when work touches any of:
- `Dockerfile`
- `deploy/ecs-task-definition.example.json`
- `deploy/iam-task-policy.example.json`
- `deploy/environment.example`
- `deploy/openwebui-provider.example.md`
- `build.sh` or `build.ps1`
- `.github/workflows/` (if CI is added)
- Any script that builds, tags, pushes, or deploys the container image

## Checklist

### Build reproducibility

- [ ] `global.json` is present and pins the .NET SDK version with `rollForward: disable`.
- [ ] `dotnet build -c Release` succeeds from a clean workspace.
- [ ] `dotnet test` passes before any image build.
- [ ] Dockerfile uses a pinned .NET version (`mcr.microsoft.com/dotnet/sdk:10.0`, `mcr.microsoft.com/dotnet/aspnet:10.0`) — not a floating major tag or `:latest`.
- [ ] Multi-stage build is preserved (build stage + runtime stage). No build tools in the runtime image.

### Required environment variables

Check `deploy/environment.example` and confirm all required variables are documented:
- [ ] `ASPNETCORE_ENVIRONMENT`
- [ ] `Gateway__AwsRegion`
- [ ] `Gateway__BedrockRuntimeEndpointDns` (VPC endpoint DNS)
- [ ] `Jwt__Authority` (Azure AD GovCloud tenant URL)
- [ ] `Jwt__Audience`
- [ ] `Gateway__RequireServiceApiKey`
- [ ] `Gateway__ServiceApiKey` (sourced from Secrets Manager reference — not plaintext)
- [ ] At least one `Gateway__Models__*` route configured with a real model ID

### Secret handling

- [ ] `Gateway__ServiceApiKey` is not hardcoded in the task definition — it must be a Secrets Manager reference or injected at deploy time.
- [ ] No AWS access keys or secret keys in `Dockerfile`, environment files, or task definition.
- [ ] No JWT secrets or tenant IDs committed in plain text.
- [ ] Secrets Manager / SSM references use the correct IAM task role permissions.

### Health checks and readiness

- [ ] `/healthz` is mapped and reachable by the ALB target group health check.
- [ ] `/readyz` is available for deployment smoke tests.
- [ ] ECS health check command does not require `curl` in the runtime image unless it is explicitly installed. Prefer ALB target health checks.
- [ ] Health check failure should cause ECS to stop the task and roll back.

### Rollback plan

- [ ] Previous ECS task definition revision is identified before deploying.
- [ ] Rollback command is documented: update ECS service to previous task definition revision.
- [ ] Blue/green or canary deployment is preferred; if not available, note the manual rollback procedure.
- [ ] Post-deploy smoke test failures should trigger rollback automatically or via on-call runbook.

### Logging and monitoring

- [ ] CloudWatch log group is configured in the ECS task definition.
- [ ] ECS execution role has `logs:CreateLogStream` and `logs:PutLogEvents` permissions.
- [ ] Log retention is set (not unlimited).
- [ ] Structured JSON logs will land correctly in the configured log group.

### Least privilege

- [ ] Task role grants Bedrock access only to the specific model ARNs or inference profiles being deployed — not `bedrock:InvokeModel` on `*`.
- [ ] Task role does not have `bedrock:ListFoundationModels` unless model discovery is enabled.
- [ ] Execution role is separate from task role.
- [ ] No `AdministratorAccess` or broad managed policies on either role.

### Network exposure

- [ ] ECS task runs in private subnets with no `AssignPublicIp: ENABLED`.
- [ ] Load balancer is internal only — not internet-facing.
- [ ] Security group allows only the ALB security group on port 8080 — not `0.0.0.0/0`.
- [ ] Required VPC endpoints are in place and their endpoint policies are least-privilege.

### Version tagging

- [ ] Image tag in the ECS task definition is a Git SHA, release tag, or other immutable identifier — not `:latest`.
- [ ] The image being deployed has been scanned (ECR image scanning or equivalent).
- [ ] SBOM generation is recommended before production deployment.

## Post-deploy smoke tests

Run these after every deployment:

```bash
# Liveness
curl -s https://<internal-alb>/healthz

# Readiness (config validation)
curl -s https://<internal-alb>/readyz

# Auth required on model list
curl -s https://<internal-alb>/v1/models   # expects 401

# Successful model list with valid JWT
curl -s https://<internal-alb>/v1/models \
  -H "Authorization: Bearer $USER_JWT"

# Policy denial — non-ITAR group requests ITAR model
# expects ITAR_MODEL_DENIED

# Successful non-ITAR completion (requires real Bedrock access)
```

## Report format

For each failed check, state:
- File and location
- The gap
- Risk if deployed as-is
- Recommended fix

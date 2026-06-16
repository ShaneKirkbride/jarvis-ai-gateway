# /project:deploy

Deployment readiness checklist for Jarvis AI Gateway.

**This command does not deploy anything automatically. It produces a checklist and stops.**
Only proceed with actual deployment steps when the user explicitly says "deploy now" or "proceed."

## Pre-deployment checklist

### 1. Build and test status

- [ ] `dotnet restore` completes without errors.
- [ ] `dotnet build -c Release` succeeds with zero errors.
- [ ] `dotnet test` passes — all tests green, 100% line coverage threshold met.
- [ ] Docker image builds successfully: `docker build -t jarvis-ai-gateway .`

### 2. Configuration review

Read `deploy/environment.example` and check:
- [ ] `Gateway__AwsRegion` is set to `us-gov-west-1` (or the correct GovCloud region).
- [ ] `Gateway__BedrockRuntimeEndpointDns` is set to a real VPC endpoint DNS name (not the example placeholder).
- [ ] `Jwt__Authority` points to the correct Azure AD GovCloud tenant (not `REPLACE_TENANT_ID`).
- [ ] `Jwt__Audience` is set to `api://jarvis-ai-gateway` (or the correct audience).
- [ ] `Gateway__RequireServiceApiKey=true` in Production.
- [ ] `Gateway__ServiceApiKey` is sourced from AWS Secrets Manager — not hardcoded.
- [ ] Model aliases have real Bedrock model IDs (no `REPLACE_WITH_*` values).
- [ ] `Gateway__ModelDiscovery__Enabled` is appropriate for the environment.

### 3. Secret handling

Check secrets without printing values:
- [ ] Confirm `Gateway__ServiceApiKey` reference points to Secrets Manager (not a plaintext value in task definition).
- [ ] No secrets visible in ECS task definition environment variable values.
- [ ] No secrets committed to git (run `git log --diff-filter=A -- '*.json' '*.env' '*.yml'` to check recent adds).

### 4. Image and tag

- [ ] Image tag is an immutable Git SHA or release tag — not `:latest`.
- [ ] Image has been scanned (ECR scan or equivalent) before deployment.
- [ ] Container runs as non-root (UID 64198 / `jarvis` user) — do not override.

### 5. IAM and networking

Read `deploy/iam-task-policy.example.json` and confirm:
- [ ] Task role has least-privilege Bedrock permissions (specific model ARN/inference profile, not `*`).
- [ ] Task role can read the required Secrets Manager secrets.
- [ ] ECS task runs in private subnets with no public IP.
- [ ] ALB/NLB is internal only.
- [ ] Required VPC endpoints are in place (Bedrock Runtime, CloudWatch Logs, Secrets Manager, ECR, KMS).

### 6. Health checks and rollback

- [ ] ALB target group health check configured against `/healthz`.
- [ ] Post-deploy smoke test plan:
  - `GET /healthz` → 200
  - `GET /readyz` → 200 (validates JWT config, model aliases, service key requirement)
  - `GET /v1/models` with a valid user JWT → returns model list
  - `POST /v1/chat/completions` with a non-ITAR model → allow
  - `POST /v1/chat/completions` to ITAR model from non-ITAR group → deny with `ITAR_MODEL_DENIED`
  - Request without auth → 401
  - Request without service key (if `RequireServiceApiKey=true`) → 401/403
- [ ] Rollback plan: previous ECS task definition revision identified; ECS can be rolled back by updating the service to the prior revision.

### 7. Logging and monitoring

- [ ] CloudWatch log group exists and ECS execution role can write to it.
- [ ] Log retention policy set (do not retain audit logs indefinitely unless compliance requires it).
- [ ] CloudWatch alarms exist or are planned for: policy denials, auth failures, 5xx rate, Bedrock errors, throttles.

## Deployment steps (only proceed when user says go)

```bash
# 1. Build and push image (replace with your ECR URI and SHA tag)
docker build -t <ecr-uri>/jarvis-ai-gateway:<git-sha> .
docker push <ecr-uri>/jarvis-ai-gateway:<git-sha>

# 2. Update ECS task definition with new image tag
# (do this via AWS Console, CDK, Terraform, or CLI — not here)

# 3. Deploy ECS service
# aws ecs update-service --cluster <cluster> --service jarvis-ai-gateway --force-new-deployment

# 4. Run smoke tests
curl https://<internal-alb-dns>/healthz
curl https://<internal-alb-dns>/readyz
```

Do not run these steps without explicit user instruction.

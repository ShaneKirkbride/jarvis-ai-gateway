# Operations Runbook

## Deploy

1. Build, scan, and push an image tagged with a Git SHA or release tag.
2. Update ECS task definition using placeholders replaced from CI/CD secrets or parameter store.
3. Confirm `Gateway__RequireServiceApiKey=true` and `Gateway__ModelDiscovery__Enabled=false` for Production.
4. Deploy to a canary or blue/green target group.
5. Check `/healthz`, `/readyz`, auth rejection, service-key rejection, model list, denied ITAR path, and one approved non-ITAR completion.

## Rollback

- Repoint ECS service to the last known-good task definition/image tag.
- Verify `/readyz` and a controlled completion.
- Preserve logs for incident review.

## Secret rotation

1. Write a new service key to Secrets Manager.
2. Deploy/restart tasks to pick up the new secret.
3. Update Open WebUI/proxy configuration.
4. Confirm old key fails and new key succeeds.

## Bedrock outage

- Check Bedrock Runtime VPC endpoint DNS and security groups.
- Confirm task role still has invoke permission for configured model IDs.
- Return client-safe 502 errors; do not retry indefinitely.

## Bad policy rollout

- Roll back task definition or configuration.
- Review audit events for denial rule IDs and affected aliases/workspaces.
- Confirm ITAR and group controls still fail closed.

## Log retention and access

- Apply CloudWatch retention according to compliance requirements.
- Restrict log read access to operations/security roles.
- Do not enable raw prompt/response logging without written approval.

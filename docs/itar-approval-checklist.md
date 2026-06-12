# ITAR Approval Evidence Checklist

For each ITAR-capable model alias, retain evidence for:

- Approved Bedrock model ID or inference profile ARN.
- AWS GovCloud region and account boundary.
- IAM policy limiting invoke access to approved resources.
- Workspace IDs approved for ITAR use.
- IdP groups authorized for ITAR access.
- Gateway configuration showing `ItarApproved=true` for the alias.
- Test evidence for `ITAR_MODEL_DENIED` and `ITAR_WORKSPACE_DENIED` denials.
- Log-retention and access-control approval.
- Incident contact and escalation owner.

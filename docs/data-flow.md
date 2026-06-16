# Data Flow

```text
User -> Open WebUI -> internal ALB (mTLS verify) -> Jarvis AI Gateway -> Bedrock Runtime VPC endpoint -> Bedrock model
                                                                    \-> Microsoft Graph (commercial Entra, group resolution, cached)
                                                                    \-> CloudWatch Logs metadata audit events -> SIEM
                                                                    \-> Secrets Manager/KMS for signing keys and audit salt at task startup
```

## Request metadata

When the identity broker is enabled:

- Identity: canonical subject (UPN preferred, email fallback), Entra `oid` resolved by Graph, assertion kind, identity source (fresh Graph call vs cached).
- Authorization input: Entra group **object IDs** resolved by Microsoft Graph. Display names are diagnostic only — never load-bearing for policy.
- Routing: requested OpenAI model alias, workspace ID, data label, ITAR mode.
- Policy: stable rule ID, group-ID match outcome, and the structured policy decision in audit only.
- Usage: prompt/output/total tokens when available.

Stable user identifiers in audit (canonical subject, Entra `oid`) are SHA-256 hashed with a deployment-wide salt. Email domain is logged in clear for tenant-level diagnostics. Group object IDs are logged in clear as authorization inputs.

The broker is the default identity path as of PR 3. Setting `Gateway:IdentityBroker:Enabled=false` reverts to the legacy JwtBearer flow, in which case metadata reflects the IdP-supplied JWT claims as previously documented.

Raw prompt and response bodies are NEVER emitted to logs. Raw tokens, signing keys, Graph client secrets, and the audit salt are NEVER logged.

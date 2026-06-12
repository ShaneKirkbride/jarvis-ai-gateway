# Data Flow

```text
User -> Open WebUI -> internal ALB/NLB -> Jarvis AI Gateway -> Bedrock Runtime VPC endpoint -> Bedrock model
                                      \-> CloudWatch Logs metadata audit events
                                      \-> Secrets Manager/KMS for service key at task startup
```

## Request metadata

- Identity: JWT `sub`, email-like claims, and configured group claims.
- Routing: requested OpenAI model alias, workspace ID, data label, ITAR mode.
- Policy: stable rule ID and human-readable reason for denials.
- Usage: prompt/output/total tokens when available.

Raw prompt and response bodies should not be emitted to logs by default.

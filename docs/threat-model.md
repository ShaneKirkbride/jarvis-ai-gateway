# Threat Model

## Data flow

Open WebUI or an identity-aware proxy sends an OpenAI-compatible request to Jarvis AI Gateway. The gateway validates JWT identity, service-to-service authentication, request shape, model policy, ITAR routing, and prompt controls before invoking Bedrock Runtime.

## Trust boundaries

1. User browser to Open WebUI.
2. Open WebUI/proxy to Jarvis AI Gateway.
3. Jarvis AI Gateway to AWS control/runtime APIs.
4. Gateway structured logs to CloudWatch Logs.

## Key threats and controls

- Spoofed user identity: validate IdP JWT issuer, audience, lifetime, and signing keys.
- Bypassed portal controls: gateway enforces model aliases, groups, and ITAR checks independently.
- Service abuse from another workload: require `X-Jarvis-Gateway-Key` in Production; consider mTLS later.
- Secret disclosure: never log service keys; source them from Secrets Manager; redact prompts before logs by default.
- ITAR data routed to non-approved model: deny with stable `ITAR_MODEL_DENIED` or `ITAR_WORKSPACE_DENIED` rule IDs.
- Prompt exfiltration through audit logs: log metadata and counts only by default, not raw prompt/response content.
- Over-permissive IAM: task role should only invoke approved model ARNs/inference profiles and read required secrets.

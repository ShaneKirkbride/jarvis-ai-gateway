# Threat Model

## Data flow

Open WebUI authenticates the user via the corporate OIDC/SAML bridge and forwards a signed OWUI-originated identity assertion on `X-Jarvis-User-Token`. The gateway validates the assertion (signature, `exp`, and `MaxAssertionAge`), resolves AD group membership independently through Microsoft Graph, then evaluates policy keyed on Entra group object IDs before invoking Bedrock Runtime.

The identity broker is the default authentication path as of PR 3. Setting `Gateway:IdentityBroker:Enabled=false` reverts the gateway to the legacy ASP.NET Core JWT bearer path described in earlier revisions of this document — used during in-flight migrations or for break-glass rollback.

## Trust boundaries

1. User browser to Open WebUI.
2. Open WebUI ↔ Jarvis AI Gateway, secured by mTLS terminated at the internal ALB in verify mode. Open WebUI is the gateway's portal identity authority.
3. Jarvis AI Gateway to Microsoft Graph (commercial Entra) — Graph is the authoritative group-membership source. Cached, never served stale on failure.
4. Jarvis AI Gateway to AWS control/runtime APIs.
5. Gateway structured logs to CloudWatch Logs → SIEM.

## Key threats and controls

- Spoofed user identity: validate the OWUI session assertion's signature, `exp`, and `MaxAssertionAge` independently. mTLS at the ALB prevents non-OWUI callers from invoking the gateway at all.
- Group spoofing by OWUI: the gateway NEVER trusts group claims from the inbound assertion. Groups are resolved independently via Microsoft Graph and policy keys on Entra group object IDs.
- Display-name collision or rename: policy never matches on display name. Renaming a group in Entra has no effect on authorization.
- Graph DoS via malicious traffic: the pre-auth rate limiter caps Graph call rate per source (IP or mTLS cert subject) BEFORE the validator chain or Graph are invoked.
- Graph outage as availability lever: the gateway fails closed (HTTP 503 `IDENTITY_RESOLUTION_UNAVAILABLE`). Stale entries are never served beyond their TTL.
- Replayed assertion: `MaxAssertionAge` bounds the replay window independently of the assertion's `exp`.
- Long-lived stale group membership: cache TTL is bounded at 5 minutes by default. Deprovisioning becomes visible within one TTL.
- Bypassed portal controls: gateway enforces model aliases, group object IDs, and ITAR checks independently regardless of what OWUI claims.
- Secret disclosure: never log raw tokens, signing keys, the Graph client secret, the audit salt, prompts, or responses. Stable user identifiers (canonical subject, Entra `oid`) are SHA-256 hashed with a configured salt.
- ITAR data routed to non-approved model: deny with stable `ITAR_MODEL_DENIED` or `ITAR_WORKSPACE_DENIED` rule IDs. ITAR-approved models cannot be configured with display-name-only authorization while the broker is enabled.
- Prompt exfiltration through audit logs: log metadata and counts only by default, not raw prompt/response content.
- Over-permissive IAM: task role only invokes approved Bedrock model ARNs/inference profiles. Microsoft Graph permissions are least-privilege (`User.Read.All` + `GroupMember.Read.All`, with `Directory.Read.All` only if verification shows it is required — see plan §6).

# Open WebUI Provider Setup

Configure Open WebUI to call the gateway as an OpenAI-compatible provider.

> Operational guide for the identity-broker path lives in
> [`docs/identity-broker.md`](../docs/identity-broker.md).  This file is the
> short reference; the broker doc is the full integration guide including
> mTLS, Graph credentials, model authorization, and smoke tests.

## Provider

- Provider name: `Jarvis GovCloud Gateway`
- Base URL: `https://jarvis-ai-gateway.internal/v1`
- API key: service-to-service key, if you use a reverse proxy that maps it to `X-Jarvis-Gateway-Key`
- Models:
  - `jarvis-govcloud-general`
  - `jarvis-govcloud-itar`

## Important identity note

The gateway accepts a **signed OWUI-originated identity assertion** on `X-Jarvis-User-Token` and resolves AD group membership independently through Microsoft Graph. See `docs/identity-broker-plan.md` for the full design.

When `Gateway:IdentityBroker:Enabled=true`:

1. User authenticates to Open WebUI using corporate SSO (Entra commercial, fronted by your corporate OIDC/SAML bridge).
2. Open WebUI forwards its signed session assertion to the gateway in the `X-Jarvis-User-Token` header. Whether this is the OWUI session JWT (current path) or a future short-lived Jarvis-minted assertion is determined by which validator is registered — the gateway is agnostic to the wire format.
3. The gateway validates the assertion (signature, `exp`, and `MaxAssertionAge`), resolves the user's Entra group object IDs via Microsoft Graph, and authorizes the request based on those object IDs.
4. The service-to-service trust between Open WebUI and the gateway is provided by **mTLS terminated at the internal ALB in verify mode**. The static `X-Jarvis-Gateway-Key` remains as a transitional belt-and-suspenders during the mTLS rollout and is removed in a follow-up release.

How Open WebUI gets `X-Jarvis-User-Token` onto outbound provider calls depends on your specific Open WebUI version. Two known paths:

- **Per-provider custom headers** (recent OWUI versions): configure the gateway provider with a custom outbound header that carries the OWUI session JWT.
- **OWUI Pipeline/Function** (older versions, or if the custom-header path is not available): install a small OWUI Pipe that injects `X-Jarvis-User-Token: <session JWT>` on every request to the gateway.

This is intentionally an Open WebUI deployment concern, not a gateway concern.

When `Gateway:IdentityBroker:Enabled=false` (the PR 1 default), the gateway remains on the legacy ASP.NET Core JWT bearer path and the original `Authorization: Bearer` / `X-Jarvis-User-Token` token semantics still apply.

## Optional request metadata

The gateway reads these headers when present:

- `X-Jarvis-Workspace-Id`
- `X-Jarvis-Data-Label`
- `X-Jarvis-Itar-Mode`
- `X-Correlation-Id`

It also reads equivalent OpenAI `metadata` fields:

```json
{
  "metadata": {
    "workspace_id": "jarvis-itar",
    "data_label": "ITAR_APPROVED",
    "itar_mode": true
  }
}
```

## Streaming

Set streaming off for this provider. The gateway currently rejects `stream=true` with a clear OpenAI-compatible HTTP 400 response until true Bedrock ConverseStream token streaming is implemented and tested.

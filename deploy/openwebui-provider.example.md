# Open WebUI Provider Setup

Configure Open WebUI to call the gateway as an OpenAI-compatible provider.

## Provider

- Provider name: `Jarvis GovCloud Gateway`
- Base URL: `https://jarvis-ai-gateway.internal/v1`
- API key: service-to-service key, if you use a reverse proxy that maps it to `X-Jarvis-Gateway-Key`
- Models:
  - `jarvis-govcloud-general`
  - `jarvis-govcloud-itar`

## Important identity note

For the gateway to independently validate the user, it needs the user's IdP-issued JWT on every request. The cleanest pattern is:

1. User authenticates to Open WebUI using corporate SSO.
2. Open WebUI or an identity-aware reverse proxy forwards a user JWT to the gateway using `X-Jarvis-User-Token`.
3. Open WebUI/reverse proxy also sends a service-to-service key using `X-Jarvis-Gateway-Key`.
4. The gateway validates issuer, audience, signature, expiry, and group claims before invoking Bedrock.

If Open WebUI can only send a static provider API key and cannot forward the user's token, then the gateway can still enforce model allowlists and Bedrock routing, but it cannot honestly claim independent per-user IdP validation. In that case, put an identity-aware proxy or small token broker between Open WebUI and the gateway.

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

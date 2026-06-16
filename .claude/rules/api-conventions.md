# API Conventions — Jarvis AI Gateway

## API surface

The gateway exposes three OpenAI-compatible endpoints:

| Endpoint | Method | Purpose |
|---|---|---|
| `/healthz` | GET | Liveness check — lightweight, no auth required |
| `/readyz` | GET | Readiness — validates JWT config, model aliases, service key, production invariants |
| `/v1/models` | GET | List permitted model aliases for the authenticated user |
| `/v1/chat/completions` | POST | Submit a non-streaming chat completion |

## Request contracts

**Do not accept what you do not understand.**
Unknown fields in the request body should be ignored by the deserializer (default `System.Text.Json` behavior), but unknown `content` part types (image, audio, tool use, mixed arrays) must be rejected with a `400` — they are not silently dropped.

**Required fields for `/v1/chat/completions`:**
- `model` — must be a known, enabled alias (not a raw Bedrock model ID unless `ExposeRawBedrockModelIds=true`).
- `messages` — must be non-empty, contain at least one user message, text-only content only.

**Rejected fields:**
- `stream: true` — rejected with `400` unless `Gateway:Streaming:FallbackToNonStreaming=true`.
- Non-text message content parts (image URLs, base64 images, audio, tool results) — rejected with `400`.

## Response contracts

Responses follow the OpenAI Chat Completions schema. Clients (Open WebUI) depend on this shape — do not break it.

**Stable fields:**
```json
{
  "id": "chatcmpl-<uuid>",
  "object": "chat.completion",
  "created": <unix timestamp>,
  "model": "<alias>",
  "choices": [
    {
      "index": 0,
      "message": { "role": "assistant", "content": "<text>" },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": <int>,
    "completion_tokens": <int>,
    "total_tokens": <int>
  }
}
```

Do not rename these fields. Do not remove them. Adding new optional fields is acceptable but must be documented.

## Error responses

All errors must use the OpenAI-compatible error envelope:

```json
{
  "error": {
    "message": "<human-readable description>",
    "type": "<error_type>",
    "code": "<stable_error_code>"
  }
}
```

Use stable `code` values for policy outcomes so clients and monitoring rules can key on them:
- `POLICY_DENIED` — generic policy denial
- `ITAR_MODEL_DENIED` — ITAR model requested by non-approved group
- `ITAR_WORKSPACE_DENIED` — ITAR model requested from non-approved workspace
- `MODEL_NOT_FOUND` — alias not in registry
- `MODEL_NOT_ENABLED` — alias exists but is disabled
- `STREAMING_NOT_SUPPORTED` — `stream=true` rejected
- `VALIDATION_ERROR` — request shape invalid

**Do not expose internal stack traces, Bedrock error details, or AWS request IDs in error responses.**

## HTTP status codes

| Status | When |
|---|---|
| 200 | Successful completion |
| 400 | Request validation failed, streaming rejected, unsupported content type |
| 401 | Missing or invalid JWT, missing service API key |
| 403 | Valid identity but policy denied (group, ITAR) |
| 404 | Model alias not found |
| 429 | Rate limit exceeded |
| 500 | Unexpected internal error |
| 501 | Model discovered but no invocation adapter available |
| 503 | Bedrock unavailable or gateway not ready |

## Correlation and tracing

- Every request should carry a correlation ID. Read `X-Correlation-Id` from the request; if absent, generate one.
- `CorrelationIdMiddleware` is the canonical place for this logic.
- Log the correlation ID on every audit event.
- Include `X-Correlation-Id` in error responses where possible so clients can reference it in support requests.

## Authentication and authorization

- Every endpoint except `/healthz` requires JWT bearer authentication.
- `/v1/models` and `/v1/chat/completions` also require the service API key (`X-Jarvis-Gateway-Key`) when `Gateway:RequireServiceApiKey=true`.
- JWT validation must check: issuer, audience, token lifetime, and signing keys. Do not disable any of these.
- Authorization (model access, ITAR) is evaluated after authentication. Never skip auth to get to policy.

## Input validation rules

Apply these at the HTTP boundary before any domain logic:
- Model alias: non-null, non-empty string, max reasonable length.
- Messages: non-null, non-empty array, each message has a `role` and `content`.
- Temperature: within `[Gateway:RequestValidation:MinimumTemperature, Gateway:RequestValidation:MaximumTemperature]`.
- Max tokens: positive integer, within model's `MaxOutputTokens` if configured.
- Stop sequences: count ≤ `MaxStopSequences`, each ≤ `MaxStopSequenceCharacters`.
- Metadata: size ≤ `MaxMetadataBytes`.
- Request body: size ≤ `Gateway:MaxRequestBodyBytes` (enforced by middleware).

## Backward compatibility

- This gateway is consumed by Open WebUI. Breaking changes to `/v1/models` or `/v1/chat/completions` response shapes will break the portal.
- Before removing or renaming a response field, verify Open WebUI does not depend on it.
- New optional request fields may be added without a breaking change.
- New optional response fields may be added without a breaking change.
- New mandatory request fields are breaking changes — document and coordinate.

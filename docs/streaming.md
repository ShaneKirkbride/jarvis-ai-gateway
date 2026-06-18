# Streaming responses (Server-Sent Events)

The gateway serves OpenAI-compatible streaming for `POST /v1/chat/completions` when the
request body sets `stream: true`. Open WebUI sends `stream: true` by default, so this is the
normal interactive path.

## Request flow

```
Open WebUI → http://127.0.0.1:18080/v1 → Envoy (mTLS) → AI Gateway → Bedrock ConverseStream
```

Streaming reuses the entire non-streaming decision pipeline. In `ChatCompletionOrchestrator`
the order is fixed and unchanged:

1. Authentication (JWT / service key / identity broker).
2. Request validation (shape, roles, content type, limits).
3. Inbound-log redaction + prompt accounting.
4. **Policy authorization** (model allowlist, group/ITAR rules).
5. Model resolution + re-validation against the resolved model.
6. **Only then** is the streaming decision made.

Because the streaming branch sits *after* policy authorization, a denial still returns its
`403` (or `404`/`400`) JSON error before any stream is opened — no SSE headers are sent for a
denied request.

## Behaviour at step 6

- A `IBedrockStreamingStrategy` that `CanHandle` the resolved model → real SSE streaming via
  Bedrock `ConverseStreamAsync` (`BedrockConverseStreamInvocationStrategy`).
- No streaming-capable strategy and `Gateway:Streaming:FallbackToNonStreaming=true` → a single
  non-streaming completion is returned as ordinary JSON (backwards-compatible fallback).
- No streaming-capable strategy and fallback disabled → `400` with code `streaming_not_supported`.

`stream: false` is unaffected and returns the existing `chat.completion` JSON.

## SSE wire format

Response headers: `Content-Type: text/event-stream`, `Cache-Control: no-cache`,
`Connection: keep-alive`, `X-Accel-Buffering: no`. The body is flushed after every event.

```
data: {"id":"chatcmpl-…","object":"chat.completion.chunk","created":…,"model":"<requested>","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

data: {"id":"chatcmpl-…","object":"chat.completion.chunk","created":…,"model":"<requested>","choices":[{"index":0,"delta":{"content":"…"},"finish_reason":null}]}

data: {"id":"chatcmpl-…","object":"chat.completion.chunk","created":…,"model":"<requested>","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]
```

- `model` is the **alias the client requested**, matching the non-streaming contract.
- Bedrock stop reasons are mapped to OpenAI finish reasons: `end_turn`/`stop_sequence` → `stop`,
  `max_tokens` → `length`, `content_filtered`/`guardrail_intervened` → `content_filter`,
  `tool_use` → `tool_calls`, anything else → `stop`.

## Errors

- A provider failure **before the first byte** (e.g. `AccessDenied`) is mapped to the standard
  OpenAI error envelope with the correct HTTP status — identical to the non-streaming path
  (`provider_access_denied` → `502`, throttling → `429`, etc.).
- A provider failure **after** the stream has started cannot change the already-sent `200`
  status, so the error envelope is emitted as a trailing `data:` event followed by `[DONE]`.
- Client disconnects are observed via `HttpContext.RequestAborted`; the stream stops and the
  audit event is recorded as `CANCELLED`.

## Audit & redaction

- One audit event is written when the stream terminates. Token counts are recorded only if
  Bedrock reported usage in the `metadata` event; otherwise they are left null.
- Raw prompt and generated text are never logged.
- Inbound redaction to Bedrock is enforced exactly as in the non-streaming path (global
  `RedactBeforeBedrock`, plus always-on for ITAR data labels / workspaces).
- Output redaction is applied **per chunk** when `Gateway:Redaction:Enabled=true`. This is
  best-effort: a secret split across two chunk boundaries cannot be matched. Inbound redaction
  remains the primary control; do not rely on per-chunk output redaction for ITAR enforcement.

## Resilience & timeouts

- Streaming intentionally bypasses the Polly retry pipeline used for non-streaming calls —
  retrying a partially-sent stream would duplicate output.
- Streaming also bypasses the fixed `Gateway:ProviderTimeoutSeconds` provider deadline; a long
  stream is normal. Cancellation is driven by request lifetime instead.

### Envoy / proxy timeouts

`stream=true` holds the upstream response open. Any proxy in front of the gateway must not
apply a short overall request timeout:

- **Envoy** default route timeout is `15s`. The OpenWebUI sidecar config
  (`openwebui-envoy/docker-entrypoint.sh`) sets `route.timeout: 0s` to disable the overall
  timeout and `stream_idle_timeout: 300s` to bound only a stalled stream.
- **ALB** idle timeout should be ≥ the longest expected generation (default `60s` is usually
  fine since bytes flow continuously, but raise it for long completions).
- The gateway remains the authoritative timeout for **non-streaming** calls via
  `Gateway:ProviderTimeoutSeconds`.

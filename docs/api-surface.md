# API Surface (authoritative)

This document reflects the **source**, not historical README text. The gateway fronts **Azure
OpenAI (Azure Government)** and/or **Amazon Bedrock (AWS GovCloud)** behind one provider-neutral
core; clients never talk to a provider directly and must not hard-code provider assumptions.

## Endpoints

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/healthz` | none | liveness/info |
| GET | `/healthz/live` | none | process liveness (probe) |
| GET | `/healthz/ready` | none | `200` ready / `503 GATEWAY_CONFIG_INVALID` with redacted config problems |
| GET | `/readyz` | none | legacy readiness (config + optional Bedrock connectivity) |
| GET | `/v1/models` | required | OpenAI-compatible model list + additive capability metadata |
| POST | `/v1/chat/completions` | required | OpenAI-compatible chat; non-streaming and SSE streaming |
| POST | `/v1/embeddings` | required | OpenAI-compatible embeddings for capability-enabled models |
| POST | `/v1/completions` | required | OpenAI-compatible completion / FIM autocomplete (capability-enabled; ITAR-gated) |
| POST | `/v1/messages` | required | Anthropic-compatible Messages API (text-only, non-streaming) |
| GET | `/openapi/v1.json` | required | OpenAPI document; only when `Gateway:Discovery:OpenApiEnabled=true` |

Tool/function calling (`/v1/chat/completions`), embeddings (`/v1/embeddings`), completion/FIM
autocomplete (`/v1/completions`), and the Anthropic Messages API (`/v1/messages`) are supported for
capability-enabled models (see below). Not yet implemented: streaming tool-call deltas (a streamed
request that includes `tools` is rejected), Anthropic streaming / tools / multimodal on
`/v1/messages`, and the Bedrock provider for embeddings/completions (Azure OpenAI only for now).
These are later phases.

## Anthropic Messages API (Phase 4)

`POST /v1/messages` accepts the Anthropic Messages request shape so Claude-native clients (Claude
Code, Zed, …) can point at the gateway. It is a **wire-format adapter** over the same chat pipeline —
the request is mapped to the provider-neutral chat request and runs through the identical
validator / group-ITAR-blocked-pattern-prompt-size policy / provider / redaction / audit stack as
`/v1/chat/completions`. No new model capability flag: it serves the same chat models.

- Text content only. `content` may be a string or an array of `{type:"text",text}` blocks; any
  other block (image, tool_use, …) is **rejected** (`invalid_request_error`). Multimodal is deferred
  and would relax the text-only ITAR control — it requires policy approval before being enabled.
- `system` (string or array of text blocks) is forwarded as a system message. `max_tokens` is
  **required** (Anthropic semantics). `stream:true` is rejected (`invalid_request_error`).
- Response: `{ id:"msg_…", type:"message", role:"assistant", content:[{type:"text",text}], model,
  stop_reason, stop_sequence, usage:{input_tokens, output_tokens} }`. `stop_reason` is mapped from
  the provider stop reason (`length`→`max_tokens`, `tool_use`/`tool_calls`→`tool_use`,
  `stop_sequence`→`stop_sequence`, otherwise `end_turn`). Output text is redacted outbound.
- Errors use the Anthropic envelope `{ "type":"error", "error":{ "type", "message" } }` (e.g.
  `invalid_request_error`, `permission_error`, `rate_limit_error`, `api_error`) — not the OpenAI
  envelope. Policy denials are a generic `permission_error`; the specific reason is audit-only.

## Completions / FIM autocomplete (Phase 3)

`POST /v1/completions` (OpenAI legacy completions; `suffix` enables fill-in-the-middle). This is the
**highest-egress** surface, so it is the most constrained:

- **Capability-gated:** the model must be configured `Gateway:Models:*:SupportsFim=true` and routed
  to a provider implementing completions (Azure OpenAI today). Otherwise `MODEL_NOT_FOUND`/unsupported.
- **ITAR disable switch:** autocomplete is **rejected for ITAR requests** by default
  (`AUTOCOMPLETE_DISABLED_FOR_ITAR`, configurable via `Gateway:Completions:DisableForItar`).
- **Aggressive context cap:** prompt+suffix is hard-capped (`Gateway:Completions:MaxContextCharacters`,
  default 8000) → `context_too_large`. Send small, intentionally-selected context — not whole files.
- `prompt` must be a single string (batched/token-array prompts rejected). Inputs run through the
  full group/ITAR/blocked-pattern/prompt-size policy and are redacted before egress; completion
  output is redacted on the way back. Audit records context size + decision only — never the text.

## Embeddings (Phase 2)

`POST /v1/embeddings` (OpenAI-compatible). Capability-gated: a model must be configured with
`Gateway:Models:*:SupportsEmbeddings=true` and routed to a provider that implements embeddings
(Azure OpenAI today; Bedrock embeddings deferred). Otherwise it fails closed
(`MODEL_NOT_FOUND` / unsupported).

- `input` is a string or array of strings (token-array inputs are rejected); `dimensions` optional.
- **Bulk source-code egress:** inputs run through the SAME group/ITAR/blocked-pattern/prompt-size
  policy as chat and are redacted before leaving the gateway. Audit records the input **count**
  only — never the input text. Do not send whole repositories; index intentionally.
- Response: `{ object:"list", data:[{ object:"embedding", embedding:[…], index }], model, usage }`.

## Tool / function calling (Phase 1)

Capability-gated and fail-closed: a model only accepts `tools`, assistant `tool_calls`, and `tool`
result messages when it advertises `supports_tools:true` (set via `Gateway:Models:*:SupportsTools`).
For any other model the text-only default is unchanged and tool usage is rejected
(`tools_not_supported`). Mapped per provider (Azure OpenAI native; AWS Bedrock Converse
`toolConfig`/`toolUse`/`toolResult`).

- Request: `tools` (function definitions) and `tool_choice` (`auto`|`none`|`required`|
  `{type:function,function:{name}}`). `tool_choice` naming an undeclared tool is rejected.
- Multi-turn agent loop: send the assistant `tool_calls` message and a `tool` role message
  (`tool_call_id` + result) back to continue. Tool descriptions, call arguments, and tool results
  are redacted/ITAR-routed like any content; JSON-schema parameters pass through unchanged.
- Response: `choices[].message.tool_calls` with `finish_reason:"tool_calls"`. Tool-call arguments
  are redacted outbound. The gateway never executes tools — the IDE does.
- Streaming with tools is **not** supported yet (`"stream":true` + `tools` → `tool_streaming_not_supported`).
- Audit records tool counts and function names only — never arguments or results.

## Authentication

Two independent credentials (see [`developer-api-keys.md`](developer-api-keys.md)):

- **Service-to-service:** `X-Jarvis-Gateway-Key: <key>` — Open WebUI / internal callers.
- **Developer/IDE:** `Authorization: Bearer jrvs_<token>` — an individual user; authorizes as that
  user with their live Entra groups.

A request to `/v1/*` is admitted iff a valid developer key **or** a valid service key **or** a
valid user identity (JWT / identity broker) is present; otherwise `401`. All paths then run the
same policy/ITAR/redaction/audit. Missing/invalid credentials fail closed.

## Streaming

`POST /v1/chat/completions` with `"stream": true` returns `text/event-stream` (SSE): an initial
role chunk, incremental `delta.content` chunks, a final chunk with `finish_reason`, then
`data: [DONE]`. Policy denial returns its normal error **before** the stream opens. When a model
cannot stream, behavior depends on `Gateway:Streaming:FallbackToNonStreaming`.

## `/v1/models` capability metadata (additive)

The base OpenAI fields (`id`, `object`, `created`, `owned_by`) are preserved; unknown fields are
ignored by OpenAI/Open WebUI clients. Additional fields:

```json
{
  "id": "jarvis2-chat",
  "object": "model",
  "owned_by": "azure-openai",
  "provider": "azure-openai",
  "display_name": "Jarvis 2 Chat",
  "supports_chat": true,
  "supports_streaming": true,
  "supports_tools": false,
  "supports_embeddings": false,
  "supports_fim": false,
  "supports_vision": false,
  "context_window": 128000,
  "max_output_tokens": 4096,
  "approved_for_itar": true
}
```

`context_window`/`max_output_tokens`/`display_name` are omitted when not configured. The
`supports_*` flags reflect what the **gateway** currently exposes (Phase 0: chat + streaming),
not raw provider features.

## Error format

All errors use the OpenAI envelope:

```json
{ "error": { "message": "<generic>", "type": "<type>", "code": "<STABLE_CODE>" } }
```

Stable codes include `INVALID_API_KEY`, `IDENTITY_RESOLUTION_UNAVAILABLE`,
`IDENTITY_USER_NOT_FOUND`, `USER_GROUP_DENIED`, `ITAR_MODEL_DENIED`, `MODEL_NOT_FOUND`,
`MODEL_NOT_IN_KEY_SCOPE`, `provider_validation_error`, `provider_auth_error`,
`provider_unavailable`, `GATEWAY_CONFIG_INVALID`. Provider/internal details are never echoed.

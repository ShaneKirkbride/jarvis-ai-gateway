# IDE & OpenAI-Compatible Client Integration

The gateway exposes an OpenAI-compatible chat API, so IDE tools and SDKs connect by pointing at
its base URL and using a **developer API key** (`Authorization: Bearer jrvs_…`). Clients are thin:
the gateway owns all provider, policy, ITAR, redaction, and audit logic. Do **not** hard-code
Azure/AWS assumptions in a client.

Base URL (example): `https://<internal-gateway-host>/v1`
(Reachability is internal — VPN/PrivateLink to the GovCloud network. The gateway is not public.)

## curl

```bash
curl -sS https://<gateway>/v1/chat/completions \
  -H "Authorization: Bearer jrvs_<token>" \
  -H "Content-Type: application/json" \
  -d '{"model":"jarvis2-chat","messages":[{"role":"user","content":"explain this function"}]}'

# streaming
curl -N https://<gateway>/v1/chat/completions \
  -H "Authorization: Bearer jrvs_<token>" -H "Content-Type: application/json" \
  -d '{"model":"jarvis2-chat","stream":true,"messages":[{"role":"user","content":"hi"}]}'

# discover models + capabilities
curl -sS https://<gateway>/v1/models -H "Authorization: Bearer jrvs_<token>"

# Anthropic-compatible Messages API (Claude-native clients: Claude Code, Zed, …)
# Text-only, non-streaming. max_tokens is required. Same models as /v1/chat/completions.
curl -sS https://<gateway>/v1/messages \
  -H "Authorization: Bearer jrvs_<token>" -H "Content-Type: application/json" \
  -d '{"model":"jarvis2-chat","max_tokens":512,"messages":[{"role":"user","content":"explain this function"}]}'
```

## Continue / Cline-style config (OpenAI-compatible provider)

```json
{
  "models": [
    {
      "title": "Jarvis (GovCloud)",
      "provider": "openai",
      "apiBase": "https://<gateway>/v1",
      "apiKey": "jrvs_<token>",
      "model": "jarvis2-chat"
    }
  ]
}
```

Tools/embeddings/FIM (codebase indexing, agent mode, autocomplete) are **future work** and not yet
supported — configure chat only for now.

## VS Code extension (first target) — direction

Thin client over the gateway's chat API:
- **Selected text only by default** — never auto-scan the workspace/repo.
- **Confirm-before-send** for the selection (and show redaction notice).
- Status bar shows the active model/provider (from `/v1/models`).
- Render responses as Markdown; diff-preview for code edits is a later enhancement.
- Store the `jrvs_` key in VS Code SecretStorage; send as `Authorization: Bearer`.
- Read `/v1/models` capability metadata to enable/disable UI (e.g. streaming).

Example local settings shape:
```jsonc
{
  "jarvis.baseUrl": "https://<gateway>/v1",
  "jarvis.model": "jarvis2-chat",
  "jarvis.sendSelectionOnly": true,
  "jarvis.confirmBeforeSend": true
}
```

## Visual Studio Professional VSIX (second target) — direction

- `AsyncPackage` with a tool window for chat and an options page (base URL, model, key reference).
- Right-click "Ask Jarvis about selection" command on the editor selection (selected text only).
- All gateway calls async/non-blocking (no UI-thread HTTP).
- Key stored via the Windows credential store / VS settings (never in source).

## Security expectations for any client

- **No automatic repo/workspace scanning.** Selected source only, by default.
- The gateway still redacts obvious secrets and enforces ITAR/group policy server-side — but
  clients must not send more context than the user intends.
- Autocomplete and embeddings (bulk source egress) are explicitly out of scope until later phases
  with their own ITAR controls.
- Treat the `jrvs_` key like a password: never commit it, never log it.

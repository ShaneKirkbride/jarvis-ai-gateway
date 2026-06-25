# Jarvis Gateway VS Code Extension Example

A minimal, defensive VS Code extension for the Jarvis AI Gateway OpenAI-compatible `/v1` API.

## What it demonstrates

- Stores the `jrvs_` developer API key in VS Code SecretStorage.
- Sends only explicitly selected text by default.
- Prompts before sending selected text.
- Enforces a configurable selection-size limit before egress.
- Calls `/v1/models` for status/model discovery and `/v1/chat/completions` for chat.
- Handles missing configuration, missing selections, cancellation, network failures, gateway errors, and malformed JSON without crashing.

## Local development

```bash
npm install
npm test
```

Press `F5` in VS Code to launch an Extension Development Host, then run:

1. `Jarvis: Configure Developer API Key`
2. Configure `jarvis.baseUrl` and `jarvis.model` in settings.
3. Select text in an editor and run `Jarvis: Ask About Selection`.

## Settings

```jsonc
{
  "jarvis.baseUrl": "https://<gateway>/v1",
  "jarvis.model": "jarvis2-chat",
  "jarvis.sendSelectionOnly": true,
  "jarvis.confirmBeforeSend": true,
  "jarvis.maxSelectionCharacters": 8000
}
```

## Production hardening ideas

- Add a webview chat transcript with explicit clear-history behavior.
- Add streamed SSE rendering once UX requirements are approved.
- Add diff preview for generated code edits rather than writing files directly.
- Add enterprise extension signing/publishing controls.

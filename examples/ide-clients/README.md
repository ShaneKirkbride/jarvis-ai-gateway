# Jarvis IDE Client Examples

This folder contains reference implementations for IDE clients that call the Jarvis AI Gateway through its OpenAI-compatible API.

- `vscode-jarvis/` — a minimal VS Code extension written in TypeScript.
- `visual-studio-jarvis/` — a Visual Studio Professional VSIX example written in C#.

Both examples are intentionally thin clients. They do not implement provider routing, policy, ITAR decisions, redaction, or audit logic; those controls remain server-side in the gateway. Both examples default to selected text only and avoid automatic repository scanning.

## Security posture

- Store developer API keys in IDE secret storage, not source files or settings JSON.
- Send credentials only as `Authorization: Bearer jrvs_...`.
- Do not log raw prompts or API keys.
- Require explicit user action before selected source is sent.
- Fail gracefully when configuration, selection, network access, or model discovery is unavailable.

## Tested in this repository

The VS Code example includes TypeScript compile-time checks and small unit tests for the gateway client request/response logic. The Visual Studio VSIX example compiles as a source sample in this repository; packaging and interactive debugging should be completed on Windows with Visual Studio 2022 and the Visual Studio extension development workload.

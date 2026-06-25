# IDE Client Improvement Backlog

These examples are intentionally small. Before using either client as a production extension, consider the following work:

## Cross-client priorities

1. Add enterprise extension signing, release provenance, and vulnerability scanning.
2. Add an approved telemetry policy that records operational events without prompts, responses, file paths, or API keys.
3. Add configurable request timeouts, retry policy for safe idempotent discovery calls, and richer offline diagnostics.
4. Add accessibility review for all UI surfaces.
5. Add an explicit privacy notice and data-classification reminder before first use.
6. Add integration tests against a local mock Jarvis gateway contract server.
7. Add generated API types from the gateway OpenAPI document once that surface is enabled for client CI.

## VS Code follow-ups

- Add an Extension Development Host integration test with `@vscode/test-electron`.
- Add streamed response rendering after SSE UX and cancellation behavior are approved.
- Add a read-only webview transcript with explicit clear-history controls.
- Add diff preview for proposed edits; never write files directly without user review.

## Visual Studio follow-ups

- Wire the command table (`.vsct`) and editor context-menu command in a Windows VSIX project.
- Replace or wrap the DPAPI sample store with the organization's approved Visual Studio credential provider.
- Add a WPF tool window for chat history with explicit clear-history controls.
- Add VS experimental-instance tests on a Windows CI agent with the Visual Studio extension workload.

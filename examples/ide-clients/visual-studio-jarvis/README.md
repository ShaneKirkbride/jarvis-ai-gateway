# Jarvis Visual Studio Professional VSIX Example

This is a reference VSIX structure for connecting Visual Studio Professional to the Jarvis AI Gateway. It demonstrates the client-side architecture and defensive gateway client logic expected from a real extension.

## Design

- `GatewayClient` is an injectable, async HTTP client for `/v1/models` and `/v1/chat/completions`.
- `JarvisOptions` keeps non-secret settings in the Visual Studio options page.
- `ISecretStore` isolates API-key persistence so a production VSIX can use Windows DPAPI, Windows Credential Manager, or Visual Studio credential APIs.
- The extension should only send selected editor text after explicit user action; do not scan solutions or repositories automatically.

## Robustness

The gateway client validates missing base URLs, placeholder URLs, missing/invalid `jrvs_` keys, empty model names, empty messages, HTTP errors, network failures, and malformed JSON. It returns generic user-safe errors and never logs secrets.

## Building

Build on Windows with Visual Studio 2022 and the Visual Studio extension development workload installed:

```powershell
msbuild Jarvis.VisualStudio.Example.csproj /restore /p:Configuration=Release
```

This repository verifies the source sample with `dotnet build`; full VSIX packaging, command-table wiring, experimental-instance debugging, and marketplace packaging should be completed on Windows with Visual Studio 2022.

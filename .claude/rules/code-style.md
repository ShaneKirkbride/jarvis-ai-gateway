# Code Style — Jarvis AI Gateway

## General philosophy

Prefer clear, boring, maintainable code. Clever code is a liability in a security-sensitive gateway. The next engineer to read this code may be doing an incident review at 2 AM.

## C# / .NET conventions

**Structure and responsibility**
- Keep classes focused on one job. A class that validates JWTs should not also invoke Bedrock.
- Keep methods short. If a method needs a comment to explain what a block does, that block probably belongs in a well-named private method or extracted class.
- Follow the existing layering: HTTP handlers → application orchestration services → domain policy/model → infrastructure adapters (Bedrock, AWS SDK, logging, config).
- Do not put Bedrock-specific request/response logic in endpoint handlers.
- Do not put HTTP response mapping logic in domain services.

**Dependency injection**
- Register all services in `Program.cs` (or extracted extension methods). No `new` on service-layer types inside handlers.
- Use constructor injection. Do not use `IServiceProvider` to resolve dependencies manually inside services.
- Use `IOptions<T>` or `IOptionsMonitor<T>` for strongly-typed configuration. Do not read raw `IConfiguration` strings in domain services.

**Async/await**
- Every I/O call is async. Never call `.Result`, `.GetAwaiter().GetResult()`, or `.Wait()` on a `Task`.
- Pass `CancellationToken` through every async call chain. Do not discard tokens from request pipelines.
- Do not use `async void` except for event handlers (none in this codebase).

**Error handling**
- Do not swallow exceptions with empty `catch` blocks. Log the exception (with structured context) and either rethrow, return a typed error result, or return an appropriate HTTP response.
- Policy failures must fail closed. A thrown exception during a policy check should result in denial, not silent permit.
- Use `ProblemDetails` / OpenAI-compatible error responses at the HTTP boundary. Do not expose stack traces to clients.
- Do not catch `OperationCanceledException` and treat it as a generic error — let it propagate or handle it explicitly.

**Logging**
- Use `ILogger<T>` with structured log message templates: `_logger.LogWarning("Policy denied {UserId} for model {Alias}", userId, alias)`.
- Never log full prompt content, response content, service API keys, JWT tokens, or any PII that is not required for audit.
- Log at `Information` for normal policy decisions and Bedrock invocations. Use `Warning` for policy denials and auth failures. Use `Error` for unexpected exceptions.
- Use `LoggerMessage.Define` for high-frequency hot paths if performance becomes a concern.

**Validation**
- Validate at the HTTP entry point. Reject invalid inputs with an OpenAI-compatible `400` response before they reach domain services.
- Do not pass unvalidated external data deep into the service layer.
- Check model alias exists and is enabled before any policy evaluation.
- Validate temperature, token limits, message count, stop sequence length, and metadata size per `Gateway:RequestLimits` configuration.

**Configuration**
- Keep all runtime values externalized. No hardcoded region names, model IDs, tenant IDs, endpoint URIs, or API keys.
- Validate required configuration at startup. A misconfigured gateway should refuse to start or at minimum fail readiness checks, not silently use empty/default values.
- Use obvious placeholder values in example configs: `REPLACE_WITH_*`, `<tenant-id>`, etc.

**Nullable reference types**
- `#nullable enable` is on. Take it seriously. Treat every `!` null-forgiving operator as a code smell requiring a comment.
- Prefer `if (x is null) throw` or null-conditional patterns over `!`.

**Records vs classes**
- Use `record` for immutable domain decisions, policy outcomes, and request context values.
- Use `class` for mutable DTOs where model binding or serialization requires a settable property.

## Non-C# files

**appsettings.json / appsettings.Development.json**
- Never put real secrets or real infrastructure values in these files.
- Development overrides go in `appsettings.Development.json` or user secrets, not `appsettings.json`.

**Dockerfile**
- Keep the multi-stage build. Do not collapse build and runtime stages.
- Keep `USER jarvis` (non-root UID 64198). Do not run as root.
- Do not `COPY` files that contain secrets.

**deploy/ examples**
- All example files must use placeholders. Never commit real model IDs, VPC endpoint IDs, account IDs, or tenant IDs.

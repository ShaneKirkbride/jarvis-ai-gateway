# Testing Rules — Jarvis AI Gateway

## Philosophy

Tests exist to protect the security, correctness, and compliance behavior of this gateway. A test that passes while the real behavior is broken is worse than no test.

## What to test

**Always add tests for:**
- New policy rules or changes to existing policy rules (`PolicyEngine`, `IPolicyEngine`).
- New or changed ITAR routing behavior.
- Auth middleware changes (`ServiceApiKeyMiddleware`, JWT configuration).
- Redaction pattern changes (`IContentRedactor`).
- Model registry changes (`IModelRegistry`, `IBedrockModelDiscoveryService`).
- Bug fixes — add a regression test that fails without the fix and passes with it.
- New `InvokeModel` adapter — test `CanHandle`, request body generation, response parsing, and unsupported model fallback.

**Explain gaps explicitly when:**
- Coverage is excluded for a file (currently: `Program.cs`, models, options, AWS SDK adapters, logger plumbing).
- A behavior is not yet testable (e.g., live Bedrock invocation) — note what needs to be done to add it.

## Testing framework and tools

- **xunit** 2.9 with `[Fact]` and `[Theory]` / `[InlineData]`.
- **Moq** 4.20 for service mocks.
- **Microsoft.AspNetCore.Mvc.Testing** for integration tests via `WebApplicationFactory<Program>`.
- **coverlet** with 100% line threshold on included files. Do not lower this threshold.

## Coverage rules

- The 100% line threshold applies to the in-scope files (currently excludes `Program.cs`, `Models/`, `Options/GatewayOptions.cs`, `Services/BedrockInvocationStrategies.cs`, `Services/BedrockModelDiscoveryService.cs`, `Services/AuditLogger.cs`).
- Adding new in-scope service classes without tests will break the coverage gate. Either add tests or explicitly add the file to `ExcludeByFile` with a comment explaining why.
- Do not remove a file from the coverage exclusion list without adding tests for it.

## Rules

**Never weaken or delete tests to make a build pass.**
If a test is failing, fix the code (or fix the test if the test is genuinely wrong), but do not delete the test or change `[Fact]` to `[Fact(Skip = "...")]` without an explicit decision and explanation.

**Add regression tests for every bug fix.**
The test must fail without the fix and pass with it. Commit both the fix and the test together.

**Do not mock security controls in integration tests.**
Use real JWT validation and real policy logic. Mock only external I/O boundaries (Bedrock client, AWS SDK) via interfaces like `IBedrockChatClient`.

**Run the narrowest test first.**
When diagnosing a failure, run the single failing test before running the full suite:
```bash
dotnet test --filter "FullyQualifiedName~PolicyEngineMatrixTests.Allow_WhenModelIsConfigured"
```
Then run the full suite before committing.

**For auth and deployment changes, include validation steps.**
Even if automated tests pass, describe the manual smoke test steps for auth failure, policy denial, and successful completion paths.

## Test organization

Tests live in `tests/Jarvis.AiGateway.Tests/`. Naming convention: `{SubjectClass}Tests.cs`.

Current test files:
- `ChatCompletionOrchestratorTests.cs`
- `DiscoveryFilterTests.cs`
- `ModelRegistryTests.cs`
- `OpenAiChatRequestValidatorTests.cs`
- `OpenWebUiContractTests.cs`
- `PolicyAndInvocationTests.cs`
- `PolicyEngineMatrixTests.cs`
- `ReadinessAndMetricsTests.cs`
- `RedactionValidationAndAdapterTests.cs`
- `ServiceFactoryAndMiddlewareTests.cs`
- `ValidationErrorMapperAndTimeoutTests.cs`

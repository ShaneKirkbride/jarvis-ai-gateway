# /project:fix-issue

Diagnose and fix a failing test, build error, or runtime issue.

## Steps

1. **Identify the failing command or error.**
   - Ask the user to paste the error output if not already provided.
   - Alternatively, run `dotnet build src/Jarvis.AiGateway/Jarvis.AiGateway.csproj -c Release` and/or `dotnet test` and capture the output.

2. **Reproduce the issue.**
   - Run the narrowest command that demonstrates the failure.
   - Note the exact error: type, message, file, line, stack trace if available.

3. **Diagnose before fixing.**
   - Read the relevant source file(s).
   - Identify the root cause. Do not guess — read the code.
   - State the root cause clearly before proposing a fix.

4. **Make the smallest safe fix.**
   - Fix only what is broken. Do not refactor surrounding code, add comments, or "improve" unrelated logic.
   - Do not weaken tests to make a build pass.
   - Do not suppress nullable warnings with `!` unless the null is genuinely impossible and you can explain why.
   - Do not catch and swallow exceptions unless that is the correct behavior with a clear log entry.
   - Do not modify authentication, authorization, ITAR routing, audit logging, or redaction behavior as a side effect of a bug fix without calling it out explicitly.

5. **Validate.**
   - Run `dotnet build src/Jarvis.AiGateway/Jarvis.AiGateway.csproj -c Release`.
   - Run `dotnet test`.
   - If the fix touches Docker or deployment, run `docker build -t jarvis-ai-gateway .` if Docker is available.

6. **Summarize.**
   Report:
   - Root cause
   - Files changed and what changed
   - Validation performed (commands run, output)
   - Any remaining known risk or follow-up items

## Safety rules

- Do not commit the fix unless the user explicitly asks.
- Do not push to `main` without explicit instruction.
- Before the fix, show a diff or description of what will change.

# /project:review

Review changed code in this repository.

## Steps

1. Run `git status` and `git diff --stat` to identify what has changed.
2. Unless the user asks for a full-repo review, focus only on changed files.
3. For each changed file, read it and review against these dimensions:

**Correctness**
- Does the logic match the intended behavior?
- Are edge cases handled (null, empty, zero, overflow, cancellation)?
- Are async methods awaited correctly? No `.Result` or `.Wait()`.
- Are policy checks still failing closed on error paths?

**Security**
- Does this touch auth, JWT validation, model allowlists, ITAR routing, service key checks, or audit logging? If so, does it preserve or strengthen those controls?
- Could any output reach logs that should be redacted?
- Any hardcoded secrets, tokens, keys, or real model/tenant IDs?
- Any SSRF risk — is every outbound URL coming from trusted configuration?
- Input validation: are untrusted inputs validated before use?

**Maintainability**
- Is the change small and focused? Or is it a large mixed-concern change?
- Does it respect the layered architecture (HTTP → services → domain → infrastructure)?
- Are new abstractions justified by actual need, or are they speculative?

**Tests**
- Is there test coverage for the changed behavior?
- If this is a bug fix, is there a regression test?
- Are any tests weakened or deleted? That requires an explicit explanation.
- Does the 100% line coverage threshold still hold for in-scope files?

**Logging and observability**
- Structured logging used (`ILogger<T>`)?
- No full prompt/response content in logs?
- Audit events preserved or improved?

**Deployment risk**
- Does this change configuration, Dockerfile, ECS task definition, or IAM policy? If so, what is the rollback plan?
- Does this change any security control (JWT, service key, ITAR routing)?

## Output format

Group findings by severity:

**CRITICAL** — security control weakened, data exposure risk, policy fails open, secrets committed
**HIGH** — correctness bug, missing auth check, swallowed exception, missing test for security behavior
**MEDIUM** — maintainability concern, missing input validation, unclear error handling, test gap for non-security code
**LOW** — style, naming, minor structural suggestions (only flag if they matter operationally)

For each finding, state:
- File and approximate line
- What the issue is
- Suggested fix (exact code where useful)

End with a one-line summary of overall risk.

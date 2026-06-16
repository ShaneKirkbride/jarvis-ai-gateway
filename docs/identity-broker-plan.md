# Identity Broker — Implementation Plan (Revision 2)

Status: draft, awaiting approval. No code lands until this plan is signed off.

This revision incorporates the changes requested in review:
abstract the assertion validator, use Entra group object IDs as the primary
authorization control, correct ALB mTLS assumptions, add a max-assertion-age
check, split rate limiting into pre-auth and post-auth, verify Graph
least-privilege permissions, pick an explicit `/v1/models` identity behavior,
hash Entra `oid` in audit events, and add an acceptance-criteria section.

## Goal

Fold the previously-external identity-aware proxy into the gateway itself.
The gateway becomes the single enforcement point that:

1. Accepts a signed, OWUI-originated identity assertion on a dedicated header.
2. Validates the assertion cryptographically (signature + lifetime + max age).
3. Independently resolves the user's AD group membership through Microsoft Graph.
4. Authorizes based on Entra **group object IDs**, not display names.
5. Fails closed when any of the above cannot be established.

## Preserved design principles (unchanged from Revision 1)

These are the parts of the prior plan we are deliberately keeping:

- Open WebUI is the gateway's portal identity authority.
- Microsoft Graph is the authoritative source of group membership.
- The gateway never trusts inbound group claims from OWUI.
- The gateway fails closed when identity or group membership cannot be established.
- OWUI-to-gateway service trust is protected by mTLS at the internal ALB.
- The static `X-Jarvis-Gateway-Key` is transitional only.
- Audit events flow CloudWatch → SIEM.
- Identity events never log raw tokens, secrets, prompts, or responses.

## Non-goals

- Open WebUI plugin/Pipeline code (delivered separately as a deployment note).
- Replacing existing regex redaction.
- Implementing Bedrock `ConverseStream`.
- Modifying the Bedrock invocation, model registry internals, or rate-limit math.

---

## 1. `IIdentityBroker` and `IIdentityAssertionValidator` service design

The validator is generalized so the gateway is **not** hard-coupled to the raw
OWUI session JWT format. The header carries a *signed OWUI-originated identity
assertion*. Which concrete assertion shape is in use is a deployment choice;
the gateway middleware doesn't know or care.

New interfaces in `src/Jarvis.AiGateway/Services/`:

```text
IIdentityAssertionValidator
  bool CanHandle(string rawAssertion)
  Task<ValidatedAssertion> ValidateAsync(string rawAssertion, CancellationToken ct)

ValidatedAssertion (record)
  IsValid           : bool
  AssertionKind     : string                // "OwuiSessionJwt" | "JarvisAssertion" | ...
  Email             : string?
  Upn               : string?
  IssuedAt          : DateTimeOffset?
  ExpiresAt         : DateTimeOffset?
  FailureReason     : enum { None, TokenMissing, TokenInvalid, TokenExpired,
                             TokenTooOld, TokenWeakIdentity, ValidatorNotFound }
```

This mirrors the existing `IInvokeModelPayloadAdapter` chain-of-responsibility
pattern used in `Program.cs:206-208`. The broker iterates registered validators
and picks the first whose `CanHandle` returns true; if none do, the request
fails closed with `ValidatorNotFound`.

Concrete implementations:

- **`OwuiSessionJwtValidator`** — landed in PR 1. Validates HS256-signed OWUI
  session JWTs against `WEBUI_SECRET_KEY`. `CanHandle` returns true for any
  three-segment JWT with `alg=HS256` whose payload contains an `email` claim.
- **`JarvisSignedAssertionValidator`** — *future*, scaffolded as an interface
  consumer only in PR 1. Validates a short-lived Jarvis-specific assertion
  minted by an OWUI Function/Pipe specifically for the gateway. Will use a
  separate signing key (or asymmetric key) and a tighter `MaxAssertionAge`
  (~60s). Adding this later requires only a new DI registration; no middleware
  or broker changes.

Broker façade composes the validator chain with the Graph resolver:

```text
IIdentityBroker
  Task<IdentityAssertionResult> ResolveAsync(string rawAssertion,
                                             CancellationToken ct)

IdentityAssertionResult (record)
  IsValid           : bool
  CanonicalSubject  : string?
  Email             : string?
  Upn               : string?
  EntraObjectId     : string?              // populated only when Graph returns it
  Groups            : IReadOnlySet<DirectoryGroupRef>   // see §5
  IdentitySource    : enum { ValidatorGraphFresh, ValidatorGraphCached,
                             Unresolved }
  AssertionKind     : string?              // surfaced from validator
  FailureReason     : enum { None, TokenMissing, TokenInvalid, TokenExpired,
                             TokenTooOld, TokenWeakIdentity, ValidatorNotFound,
                             GraphLookupFailed, GraphUserNotFound }
```

DI: validators registered as singletons via `services.AddSingleton<IIdentityAssertionValidator, ...>()`.
Broker, Graph resolver, and middleware all singletons.

## 2. Middleware pipeline and rate limiting

The pipeline gains a pre-auth limiter so a malicious or broken client cannot
force unbounded Graph lookups before user-level rate limiting can engage. The
existing per-user limiter moves to post-auth.

```text
CorrelationIdMiddleware
ServiceApiKeyMiddleware            // transitional belt-and-suspenders;
                                   //   removed in PR 4 once mTLS is verified
(implicit) mTLS verify at ALB      // out-of-process; see §7

PreAuthRateLimiter                 // NEW — protects token validation + Graph
IdentityBrokerMiddleware           // NEW — replaces the old JwtBearer pipeline
PostAuthRateLimiter                // EXISTING per-user fixed window, moved

UseAuthorization
```

**Pre-auth limiter (new policy `"pre-auth"`)**

- Partition key: client IP (or, when mTLS is on, the client cert subject /
  serial — see §7). Falls back to IP if no cert metadata is present.
- Bucket: fixed window, intentionally tight. Defaults: `PermitLimit=60`,
  `WindowSeconds=10`, `QueueLimit=0`. Configurable under
  `Gateway:IdentityBroker:PreAuthRateLimit`.
- Applies to `/v1/models` and `/v1/chat/completions`. Skipped on `/healthz`
  and `/readyz`.
- 429 responses emitted by this limiter carry audit event
  `identity.preauth.rate_limited`.

**Post-auth limiter** — the existing `"per-user"` policy at
`Program.cs:98-113`, unchanged behavior. Just moves later in the pipeline so
its partition key (`sub` claim) is populated by the broker first.

The existing `AddJwtBearer(...)` configuration is removed. JwtBearer was
validating an IdP JWT that Open WebUI does not forward; `IdentityBrokerMiddleware`
replaces it entirely. The previous `Authorization: Bearer` fallback at
`Program.cs:80-85` is removed — `X-Jarvis-User-Token` is the single source.

On success the middleware:

1. Sets `HttpContext.User` to a `ClaimsPrincipal` carrying canonical subject,
   email, UPN (if present), Entra `oid` (if Graph returned it), and one custom
   `jarvis:group_id` claim per resolved Entra group object ID. Display name
   is attached as a parallel `jarvis:group_name` claim (diagnostic only —
   policy never reads it).
2. Emits the `identity.resolved` audit event (see §8).
3. Calls `next(context)`.

On failure the middleware short-circuits with the mapped HTTP status (see §6)
and an OpenAI-compatible error envelope. No further middleware runs.

## 3. Accepted identity header contract

```text
Header:        X-Jarvis-User-Token
Required on:   /v1/models, /v1/chat/completions
Format:        A signed OWUI-originated identity assertion.
               The wire format is opaque to the gateway — the validator
               chain (§1) decides whether it is a current OWUI session JWT,
               a future Jarvis-specific assertion, or another approved type.
Lifetime:      Validated against `exp` AND a separate max-age bound (see §4).
                2-minute clock-skew tolerance.
Audience:      Optional — enforced when configured per validator.
Issuer:        Optional — enforced when configured per validator.
```

Missing header → 401 `IDENTITY_TOKEN_MISSING`.
Multiple `X-Jarvis-User-Token` headers → 401 `IDENTITY_TOKEN_INVALID`.
`Authorization: Bearer` is **not** accepted as an identity carrier.

The static `X-Jarvis-Gateway-Key` remains via `ServiceApiKeyMiddleware` as
transitional belt-and-suspenders. Removed in PR 4 once mTLS is verified in
production.

## 4. Lifetime validation — `exp` and `MaxAssertionAge`

The gateway enforces **two** independent lifetime checks. Both must pass.

```text
1. exp must exist and the assertion must not be expired (with clock skew).
2. iat must exist and (now - iat) must not exceed MaxAssertionAgeSeconds.
```

This protects against long-lived or stolen assertions whose `exp` was set far
in the future. Validators that cannot produce both `iat` and `exp` are
rejected with `TokenWeakIdentity`.

Configuration:

```text
Gateway:IdentityBroker:ClockSkewSeconds          default 120
Gateway:IdentityBroker:MaxAssertionAgeSeconds    default 900
```

For the future `JarvisSignedAssertionValidator`, the recommended assertion
TTL is 60 seconds with `MaxAssertionAgeSeconds = 60`. This is enforced
per-validator (each validator can override the default), to allow OWUI's
session JWT to use a longer window during transition while the Jarvis-minted
assertions stay tight.

## 5. Entra `oid` / UPN / email normalization, Graph lookup, and cache

### Canonical subject

OWUI's session JWT realistic claim availability:

- `email` — reliably present.
- `name` — present, not used for identity decisions.
- `id` — OWUI's *internal* user id. Not used downstream.
- `upn` — best-effort, only if the corporate OIDC/SAML bridge passes it
  through and OWUI retains it.
- `oid` — generally **not** present on OWUI's session JWT. Populated only
  by Graph lookup, never trusted from the inbound token.

Canonical subject selection, first non-empty wins:

```text
1. upn   (if present)
2. email (required, else TokenWeakIdentity)
```

Both normalized: trimmed, lowercased, surrounding quotes stripped. Email is
length-capped at RFC 5321's 254 chars.

### Group representation

```text
DirectoryGroupRef (record)
  Id          : string    // Entra group object ID (GUID) — authorization key
  DisplayName : string?   // for human-readable diagnostics, never a policy input
```

**Authorization is keyed exclusively on `Id`.** Display names are immutable
neither in Entra nor across tenants and must not be load-bearing for ITAR
or elevated model access. This is the core security change in this revision.

### Policy and model-config implications

`Gateway:Models[].AllowedGroups` (display-name strings) is replaced with
`Gateway:Models[].AllowedGroupIds` (Entra group object IDs, GUIDs). The
`PolicyEngine` matches on `DirectoryGroupRef.Id` against the configured
allowlist.

`Gateway:Models[].AllowedGroups` remains accepted **only as a temporary
diagnostic alias** during config migration. It is **forbidden** for any
model with `ItarApproved=true`; startup validation rejects the configuration.
Removed entirely in PR 4 alongside the static gateway key.

### Graph client

- SDK: `Microsoft.Graph` v5.x (commercial cloud — `graph.microsoft.com`).
- Auth: client credentials flow via MSAL against
  `login.microsoftonline.com/<tenantId>`.
- Permissions: see §6 (verification step — not yet locked).
- Secret: from AWS Secrets Manager at startup. Certificate-based auth is
  follow-on hardening.

### Graph query

```text
GET /users/{canonicalSubject}/transitiveMemberOf
    ?$select=id,displayName
    &$top=200
```

Results filtered server-side to `microsoft.graph.group` only. Each result
yields one `DirectoryGroupRef`. `transitiveMemberOf` covers nested-group
membership.

### Cache

`IMemoryCache` (already registered in `Program.cs:93`).

```text
Key (positive):   $"groups:{canonicalSubject}"
Key (negative):   $"groups-miss:{canonicalSubject}"
Positive TTL:     Gateway:IdentityBroker:Graph:CacheSeconds          default 300
Negative TTL:     Gateway:IdentityBroker:Graph:NegativeCacheSeconds  default 30
Graph call timeout: Gateway:IdentityBroker:Graph:TimeoutSeconds      default 10
Stale serve:      NEVER. On Graph failure, no stale entry is returned.
```

A `SemaphoreSlim` keyed by canonical subject coalesces concurrent lookups so
a thundering herd for one user results in one Graph call.

### Metrics

On `Jarvis.AiGateway` meter via `IGatewayMetrics`:

```text
identity_lookup_cache_hits_total
identity_lookup_graph_calls_total
identity_lookup_failures_total{reason}
identity_lookup_latency_ms              (histogram)
identity_preauth_rate_limited_total
```

## 6. Microsoft Graph least-privilege permission verification (OPEN ITEM)

The prior revision named `User.Read.All` + `GroupMember.Read.All`. Before
those land in `docs/iam-matrix.md` they must be verified end-to-end against
the actual query:

```text
GET /users/{id-or-upn}/transitiveMemberOf?$select=id,displayName
```

**Verification step (must complete before PR 1 merges):**

1. Provision a test app registration with `User.Read.All` + `GroupMember.Read.All`
   only.
2. Confirm the query returns both `id` and `displayName` for every group the
   test user is a transitive member of.
3. If the response is missing either field, falls back, or is rejected with
   `Authorization_RequestDenied`, escalate to `Directory.Read.All` as a
   fallback and document the tenant-admin approval needed.

The final permission set, with documented evidence, becomes the
`docs/iam-matrix.md` Entra row. Until verification completes, the iam-matrix
section is marked TBD.

## 7. mTLS between OWUI and gateway — ALB verify mode

This section is corrected from Revision 1. The gateway design is built for
**ALB mTLS verify mode**, not passthrough.

### Verify mode behavior (the design we're building for)

- ALB validates the client cert against an internal CA trust store at the
  edge. Unverified connections are rejected by ALB (HTTP 495); the request
  never reaches the container.
- ALB does **not** forward the full client cert to the target in verify mode.
  It forwards a small set of metadata headers about the validated cert,
  including subject, issuer, serial number, and validity dates.
- The gateway therefore **does not** perform certificate validation itself,
  and **does not** parse a PEM body from `X-Amzn-Mtls-Clientcert`.
- The gateway **may** perform defense-in-depth identity checks on the
  ALB-forwarded metadata headers — for example, pinning the certificate
  subject (CN/SAN as exposed in the subject header) or serial number against
  a configured allowlist.

Concrete checks the gateway may perform in verify mode (all optional, all
configurable):

```text
Gateway:IdentityBroker:Mtls:Mode = "AlbVerify"                          default
Gateway:IdentityBroker:Mtls:RequireSubjectCheck                         default true (as of PR 4)
Gateway:IdentityBroker:Mtls:AcceptedClientCertSubjects   string[]        default []
Gateway:IdentityBroker:Mtls:AcceptedClientCertSerials    string[]        default []
```

When `RequireSubjectCheck=true` (now the default), mismatched (or missing)
subject metadata → 401, audited as `mtls.subject_unexpected`. Default-on as
of PR 4; readiness fails closed when no subjects or serials are configured.

### Passthrough mode (NOT the chosen design)

For completeness — if a future deployment switches to ALB mTLS passthrough,
ALB forwards the raw client cert in `X-Amzn-Mtls-Clientcert` without
validation, and the gateway must do the full validation in Kestrel. That is
out of scope for this PR. If the topology ever changes, the gateway's mTLS
section needs a new `KestrelMtlsValidator` implementation; this is called
out in `docs/runbook.md` so the assumption is explicit.

### Rotation

Rotation is operational and out of scope for the gateway code. Documented
in `docs/runbook.md`. Cert distribution into OWUI's Fargate task is
OWUI-side.

## 8. CloudWatch audit events

All audit events carry correlation ID, timestamp, request ID, source IP.
None carry the raw token, signing keys, prompt content, or response content.

Stable user identifiers (canonical subject and Entra `oid`) are **hashed**
before being logged. Hash: SHA-256, salted with
`Gateway:IdentityBroker:AuditSubjectSalt` (a secret from Secrets Manager).
Email *domain* (everything after the last `@`) is logged in clear for
tenant-level diagnostics. Group `Id` is logged in clear (it is an
authorization input, not a user identifier).

Justification for hashing Entra `oid`: although `oid` is not an email
address, it is a stable cross-system user identifier. Hashing it in our
audit pipeline reduces the impact of log exfiltration while preserving
correlation within our own SIEM (the salt is consistent across events from
the same gateway deployment, so analysts can group by hashed `oid`).

| Event | Level | Payload |
|---|---|---|
| `identity.token.missing` | warn | path, method |
| `identity.token.invalid` | warn | path, method, validator-reported reason (no token contents) |
| `identity.token.expired` | warn | path, method, `iat`/`exp` (epoch only) |
| `identity.token.too_old` | warn | path, method, `iat` (epoch only), configured max age |
| `identity.token.weak` | warn | path, method, which canonical-subject claims were missing |
| `identity.validator.not_found` | warn | path, method, assertion structure hint (e.g., "jwt-3-segment", "unknown") |
| `identity.lookup.cache_hit` | debug | hashed subject, group count |
| `identity.lookup.graph_call` | info | hashed subject, latency ms, http status |
| `identity.lookup.failed` | error | hashed subject, reason, retry-eligible bool |
| `identity.user_not_found` | warn | hashed subject |
| `identity.resolved` | info | hashed subject, email domain only, hashed oid, group count, list of group Ids, identity source, assertion kind |
| `identity.preauth.rate_limited` | warn | partition key (IP or hashed cert subject), policy name |
| `mtls.subject_unexpected` | warn | observed subject header, allowed subjects list |

Existing `policy.allow` / `policy.deny` events gain `identity_source` and
`assertion_kind` fields so a reviewer can see whether the decision was based
on a fresh Graph lookup or a cached one, and which assertion type was in
play.

## 9. Threat model and data-flow updates

### `docs/threat-model.md`

- **Data flow (§3)** — rewrite: "Open WebUI authenticates the user via the
  corporate OIDC/SAML bridge and forwards a signed OWUI-originated identity
  assertion on `X-Jarvis-User-Token`. The gateway validates the assertion
  (signature, `exp`, and `MaxAssertionAge`), resolves AD group membership
  via Microsoft Graph, then evaluates policy keyed on Entra group object IDs."
- **Trust boundaries (§7)** — add: "OWUI ↔ Gateway hop is secured by mTLS
  terminated at the internal ALB in verify mode. OWUI is the gateway's portal
  identity authority. Microsoft Graph is the group-membership authority.
  Entra group object IDs are the authorization input; display names are
  diagnostic only."
- **Key threats and controls (§14)**:
  - "Spoofed user identity": validate signed assertion (signature + `exp` +
    `MaxAssertionAge`); reject if canonical subject claims missing;
    ALB-verified mTLS prevents non-OWUI callers.
  - "Group spoofing by OWUI": never trust group claims from the inbound
    assertion; groups are resolved independently via Graph; policy matches
    on group object ID.
  - "Display-name collision or rename": policy never matches on display name;
    rename of an Entra group has no effect on authorization.
  - "Graph DoS via malicious traffic": pre-auth rate limiter caps Graph call
    rate per source before identity is established.
  - "Graph outage as availability lever": fail closed (503); no stale serve
    beyond TTL.
  - "Long-lived stale group membership": positive cache TTL ≤ 5 minutes by
    default; deprovisioning visible within one TTL.
  - "Replayed assertion": `MaxAssertionAge` bounds replay window
    independently of `exp`.

### `docs/data-flow.md`

- Add Microsoft Graph as an outbound dependency from the gateway with the
  egress path (NAT or proxy).
- Update the diagram to show OWUI as the inbound identity authority and
  Graph as the group authority.
- Note that group object IDs are the unit of authorization.

### `deploy/openwebui-provider.example.md`

- Replace "Important identity note" with the new contract: `X-Jarvis-User-Token`
  carries a signed OWUI-originated identity assertion; gateway validates it
  and resolves groups via Graph.
- Add: "Whether OWUI forwards the header via native per-provider custom
  headers or via an OWUI Pipeline/Function is an OWUI deployment concern,
  not a gateway concern."

## 10. Tests

Coverage gate stays at 100% line on in-scope files. New service files added
to the gate; Graph SDK adapter wiring excluded behind an interface.

**`OwuiSessionJwtValidatorTests.cs`** (new)

- Valid HS256 token with required claims → `IsValid=true`, normalized
  email/UPN.
- Missing or malformed signature → `TokenInvalid`.
- Different signing key → `TokenInvalid`.
- Expired by more than clock skew → `TokenExpired`.
- Expired by less than clock skew → `IsValid=true`.
- `iat` older than `MaxAssertionAge` → `TokenTooOld`.
- `iat` missing → `TokenWeakIdentity`.
- Missing both `email` and `upn` → `TokenWeakIdentity`.
- Issuer/audience configured and mismatched → `TokenInvalid`.
- Token with two segments / not a JWT → `CanHandle` returns false.

**`IdentityBrokerValidatorChainTests.cs`** (new)

- Single validator registered, handles the assertion → broker uses it.
- Single validator registered, does not handle → `ValidatorNotFound`.
- Multiple validators registered, first matching wins.
- Validator throws → broker treats as `TokenInvalid` (fail-closed, not 500).

**`GraphGroupResolverTests.cs`** (new)

- Lookup success → Graph called once, returns `DirectoryGroupRef` list with
  `Id` and `DisplayName`.
- Second lookup within TTL → no Graph call, cache hit.
- Lookup after TTL → Graph called again.
- Graph returns 404 user-not-found → negative cache populated; second
  lookup within negative TTL returns failure without a Graph call.
- Graph 5xx → `GraphLookupFailed`, no cache entry, retry-eligible.
- Graph timeout → `GraphLookupFailed`, observable via metric.
- Concurrent lookups same subject → one Graph call (semaphore coalescing).
- `Id` values preserved exactly as Graph returns them (no normalization);
  policy match must be exact.

**`IdentityBrokerTests.cs`** (new)

- Valid assertion + Graph response → `IsValid=true`, `Groups` populated with
  `DirectoryGroupRef`s, identity source `ValidatorGraphFresh`.
- Valid assertion + Graph cached → identity source `ValidatorGraphCached`.
- Valid assertion + Graph failure → `IsValid=false`,
  reason `GraphLookupFailed`.
- Invalid assertion → broker never calls Graph (verified via mock).

**`IdentityBrokerMiddlewareTests.cs`** (new)

- Header missing → 401, body `IDENTITY_TOKEN_MISSING`.
- Header present, no validator handles → 401, `IDENTITY_TOKEN_INVALID`,
  audit `identity.validator.not_found`.
- Token expired → 401, `IDENTITY_TOKEN_EXPIRED`.
- Token too old (iat outside `MaxAssertionAge`) → 401, `IDENTITY_TOKEN_TOO_OLD`.
- Graph fails → 503, `IDENTITY_RESOLUTION_UNAVAILABLE`.
- Graph success → `next` called, `HttpContext.User` carries
  `jarvis:group_id` claims, no `jarvis:group_name` is matched by policy.
- `/healthz` and `/readyz` skip the middleware.
- Pre-auth limiter trips before identity broker runs (verified by mock
  validator never being called when limiter rejects).

**`PolicyEngineGroupIdAuthorizationTests.cs`** (new or extended)

- User has matching `Id` → allowed.
- User has matching `DisplayName` but **not** matching `Id` → **denied**
  (proves display name is not a policy input).
- ITAR model with legacy `AllowedGroups` (display names) in config →
  startup validation refuses to load.

**Integration updates to existing tests**

- `OpenWebUiContractTests.cs` — replace IdP-JWT setup with OWUI-assertion
  setup and a mocked `IGraphGroupResolver` returning `DirectoryGroupRef`s.
- `PolicyAndInvocationTests.cs` — ITAR-approved user via mocked Graph (group
  `Id` match) → ITAR model OK; non-ITAR user → 403 via existing flow.
- `ReadinessAndMetricsTests.cs` — `/readyz` fails when `WEBUI_SECRET_KEY` is
  absent, when Graph credentials are absent, when `AuditSubjectSalt` is
  absent, and when any ITAR-approved model has `AllowedGroups` (display
  names) instead of `AllowedGroupIds`.
- `ValidationErrorMapperAndTimeoutTests.cs` — confirm new `IDENTITY_*` error
  codes round-trip through the OpenAI error envelope.

**Manual smoke tests in the runbook (not automated):**

- Send a valid OWUI assertion against a real test-tenant Graph and confirm
  group resolution returns `Id` + `DisplayName`.
- Trigger Graph throttling (or simulate) and confirm 503 + audit event.
- Rotate `WEBUI_SECRET_KEY` and confirm old assertions fail.
- Verify pre-auth limiter trips before token validation by sending a flood
  from one IP without a token (should 429, not 401).

## `/v1/models` identity behavior — explicit choice

**Decision: Option A — `/v1/models` requires user identity.**

- `/v1/models` requires `X-Jarvis-User-Token`. Same validation path as
  `/v1/chat/completions`. Returns only the model aliases the authenticated
  user is authorized to see (existing `PolicyEngine.GetVisibleModelsAsync`).
- This preserves ITAR-aware visibility — non-ITAR-approved users never
  see ITAR-approved aliases on the list.

**Risk and verification (must be confirmed in PR 2):**

Some Open WebUI deployments call `/v1/models` during provider setup, model
refresh, or background health checks **without** a user session token in
context. If the deployed OWUI version does that and we require identity on
`/v1/models`, provider setup or background refresh will fail with 401.

Verification step in PR 2:

1. Configure OWUI to talk to the gateway with the new identity header.
2. Confirm OWUI calls `/v1/models` *only* in user-session contexts.
3. If OWUI calls `/v1/models` in a service context (no user session), fall
   back to Option B (`/v1/models` accepts a service identity and returns
   only non-sensitive generic aliases). That fallback is a configuration
   change, not a code change — the gateway code in PR 1 supports both modes
   via `Gateway:IdentityBroker:ModelsEndpointRequiresUser` (default `true`).

## Acceptance criteria

PR 1 may not merge until **all** of the following are demonstrably true:

- [ ] Gateway no longer assumes it receives a raw OWUI session JWT. The
      validator interface (`IIdentityAssertionValidator`) is registered with a
      `OwuiSessionJwtValidator` impl, and the broker invokes it through the
      generic `CanHandle`/`ValidateAsync` contract.
- [ ] Identity assertion validation is abstracted — middleware code does not
      reference any OWUI-specific class, claim, or secret directly.
- [ ] Graph-resolved group **object IDs** are the primary authorization
      input. `Gateway:Models[].AllowedGroupIds` is the new contract.
- [ ] Display names are diagnostic only. A test asserts that a user with a
      matching display name but mismatching `Id` is denied. Startup
      validation rejects ITAR-approved models configured with legacy
      `AllowedGroups` (display names).
- [ ] ALB mTLS documentation matches actual AWS ALB behavior. The plan
      explicitly distinguishes verify vs passthrough modes; the gateway
      code targets verify mode only.
- [ ] Assertions enforce both `exp` and `MaxAssertionAge`. Tests cover both
      independently (an unexpired-but-too-old token must be rejected).
- [ ] Pre-auth rate limiting protects Graph lookup. A flood of requests
      without a token returns 429 *before* any validator is invoked
      (verified via mock validator that asserts non-invocation).
- [ ] Graph permission requirements are verified end-to-end against a test
      app registration (§6) or tracked as an explicit pending approval in
      `docs/iam-matrix.md`. The doc carries evidence.
- [ ] `/v1/models` identity behavior is explicitly chosen (Option A by
      default) and the OWUI verification step is on the PR 2 checklist.
- [ ] Stable user identifiers (canonical subject, Entra `oid`) are hashed
      in audit logs. Email domain and group `Id` are logged in clear with
      stated justification.
- [ ] No request reaches `IChatCompletionOrchestrator` without a validated
      identity and Graph-resolved groups on protected routes. Verified by
      integration test against the real middleware pipeline.
- [ ] Threat model (`docs/threat-model.md`) and data flow (`docs/data-flow.md`)
      reflect the OWUI-as-portal-identity-authority + Graph-as-group-authority
      design and the corrected mTLS section.

---

## File-level change summary

**New files**

```text
src/Jarvis.AiGateway/Services/IIdentityAssertionValidator.cs
src/Jarvis.AiGateway/Services/IIdentityBroker.cs
src/Jarvis.AiGateway/Services/IdentityBroker.cs
src/Jarvis.AiGateway/Services/OwuiSessionJwtValidator.cs
src/Jarvis.AiGateway/Services/GraphGroupResolver.cs
src/Jarvis.AiGateway/Services/DirectoryGroupRef.cs       (record, plain POCO)
src/Jarvis.AiGateway/Security/IdentityBrokerMiddleware.cs
src/Jarvis.AiGateway/Options/IdentityBrokerOptions.cs
tests/Jarvis.AiGateway.Tests/OwuiSessionJwtValidatorTests.cs
tests/Jarvis.AiGateway.Tests/IdentityBrokerValidatorChainTests.cs
tests/Jarvis.AiGateway.Tests/GraphGroupResolverTests.cs
tests/Jarvis.AiGateway.Tests/IdentityBrokerTests.cs
tests/Jarvis.AiGateway.Tests/IdentityBrokerMiddlewareTests.cs
tests/Jarvis.AiGateway.Tests/PolicyEngineGroupIdAuthorizationTests.cs
docs/identity-broker.md                                   (OWUI deployment note — stub until OWUI capability is confirmed in PR 2)
```

**Modified files**

```text
src/Jarvis.AiGateway/Program.cs                           (remove JwtBearer, register broker + validators, wire pre-auth + post-auth limiters)
src/Jarvis.AiGateway/Options/GatewayOptions.cs            (add IdentityBroker sub-options; rename AllowedGroups to AllowedGroupIds on model routes)
src/Jarvis.AiGateway/Services/PolicyEngine.cs             (match on group object ID; reject ITAR config that uses display names)
src/Jarvis.AiGateway/Services/AuditLogger.cs              (new identity.* events, hashed subjects, group Id list)
src/Jarvis.AiGateway/Services/GatewayReadinessCheck.cs    (new readiness checks: signing key, Graph creds, audit salt, ITAR config shape)
src/Jarvis.AiGateway/Services/UserContextFactory.cs       (read jarvis:group_id claims, not display names)
deploy/openwebui-provider.example.md                      (new contract)
docs/threat-model.md                                      (per §9)
docs/data-flow.md                                         (per §9)
docs/iam-matrix.md                                        (Entra app registration row — pending §6 verification)
docs/runbook.md                                           (mTLS verify-mode rotation, OWUI key rotation, Graph outage, audit salt rotation)
README.md                                                 (paragraph update on identity model)
tests/Jarvis.AiGateway.Tests/OpenWebUiContractTests.cs
tests/Jarvis.AiGateway.Tests/PolicyAndInvocationTests.cs
tests/Jarvis.AiGateway.Tests/ReadinessAndMetricsTests.cs
tests/Jarvis.AiGateway.Tests/ValidationErrorMapperAndTimeoutTests.cs
```

**Configuration additions** (all under `Gateway:IdentityBroker`)

```text
Enabled                              bool, default true as of PR 3 (was false in PR 1)
TokenHeader                          string, default "X-Jarvis-User-Token"
ClockSkewSeconds                     int, default 120
MaxAssertionAgeSeconds               int, default 900
ModelsEndpointRequiresUser           bool, default true       (Option A)
AuditSubjectSalt                     string, secret — Secrets Manager

OwuiSessionJwt:Enabled               bool, default true
OwuiSessionJwt:SigningKey            string, secret — Secrets Manager
OwuiSessionJwt:Issuer                string, optional
OwuiSessionJwt:Audience              string, optional
OwuiSessionJwt:MaxAssertionAgeSeconds int, optional override

JarvisAssertion:Enabled              bool, default false (future)
JarvisAssertion:SigningKey           string, secret — Secrets Manager (future)
JarvisAssertion:Issuer               string, optional (future)
JarvisAssertion:Audience             string, optional (future)
JarvisAssertion:MaxAssertionAgeSeconds int, optional, recommended 60 (future)

Graph:TenantId                       string
Graph:ClientId                       string
Graph:ClientSecret                   string, secret — Secrets Manager
Graph:CacheSeconds                   int, default 300
Graph:NegativeCacheSeconds           int, default 30
Graph:TimeoutSeconds                 int, default 10

PreAuthRateLimit:PermitLimit         int, default 60
PreAuthRateLimit:WindowSeconds       int, default 10
PreAuthRateLimit:QueueLimit          int, default 0
PreAuthRateLimit:PartitionBy         enum { Ip, MtlsSubject, Auto }, default Auto

Mtls:Mode                            enum { AlbVerify, KestrelMtls }, default AlbVerify
Mtls:RequireSubjectCheck             bool, default true as of PR 4 (was false in PR 1)
Mtls:AcceptedClientCertSubjects      string[], default []
Mtls:AcceptedClientCertSerials       string[], default []
```

`Gateway:Models[]` schema change:

```text
RequiredGroupIds : string[]   // NEW — Entra group object IDs (GUIDs)
AllowedGroups    : string[]   // DEPRECATED — display names; forbidden when ItarApproved=true
```

## Rollout plan

1. **PR 1** (this plan) — land all new code behind
   `Gateway:IdentityBroker:Enabled=false`. Existing JwtBearer flow stays
   alongside until PR 3. CI green. Production unchanged. Acceptance criteria
   above must all be met.
2. **PR 2** — confirm OWUI deployment path (custom headers vs Pipeline) and
   verify `/v1/models` is only called in user-session contexts (§"`/v1/models`
   identity behavior"). Ship `docs/identity-broker.md` with the chosen OWUI
   instructions.
3. **PR 3** — enable `IdentityBroker` in staging. Integration smoke against
   staging Graph. Remove JwtBearer registration entirely once green.
4. **PR 4** — turn on ALB mTLS verify mode in staging, then production.
   Remove `X-Jarvis-Gateway-Key` requirement and legacy `AllowedGroups`
   display-name support one release later.

## Open items still to confirm before PR 1 begins

Tracked in memory `project-identity-broker-open-gaps.md`. None block writing
this plan; all should be answered before code lands.

1. **Bridge claim passthrough** — does the corporate OIDC/SAML bridge expose
   Entra `upn` to OWUI, or only `email`?
2. **OWUI capability** — does the deployed OWUI version support per-provider
   custom headers, or do we need a Pipeline/Function?
3. **NAT/PrivateLink path** for `graph.microsoft.com` and
   `login.microsoftonline.com` egress from the gateway's subnet.
4. **Internal CA selection** for mTLS client certs (ACM Private CA vs
   existing internal PKI).
5. **Graph least-privilege permission verification** (§6) — confirm
   `User.Read.All` + `GroupMember.Read.All` is sufficient for
   `/users/{id}/transitiveMemberOf?$select=id,displayName`. Fall back to
   `Directory.Read.All` only if needed.
6. **OWUI `/v1/models` calling pattern** — confirm `/v1/models` is called
   only with a user session attached, so Option A is viable.
7. **Existing Entra group object IDs** — for each `AllowedGroups` value
   in current config (`AI-General-Users`, `AI-ITAR-Approved`), obtain the
   corresponding Entra group object ID for the new `AllowedGroupIds` config.

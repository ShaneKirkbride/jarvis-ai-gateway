# Identity Broker — Open WebUI Integration

This document is the operational counterpart to
[`identity-broker-plan.md`](identity-broker-plan.md). The plan describes WHY the gateway's
identity broker exists and HOW it's built; this document explains how to wire Open WebUI
to talk to a broker-enabled gateway.

Target Open WebUI version: **latest stable from
[open-webui/open-webui](https://github.com/open-webui/open-webui)** (v0.8.6 or later as of
this writing). The doc tracks upstream, not an older pinned release.

---

## 1. Choosing an integration path

Two paths are supported. Pick exactly one per deployment.

### Path A — Trusted-header (recommended, config-only)

Open WebUI v0.8.6+ has a native feature that forwards a small set of identity headers to
external OpenAI-compatible providers when `ENABLE_FORWARD_USER_INFO_HEADERS=true`:

- `X-OpenWebUI-User-Email`
- `X-OpenWebUI-User-Id`
- `X-OpenWebUI-User-Name`
- `X-OpenWebUI-User-Role`

The gateway's `OwuiTrustedHeaderValidator` accepts these headers as the identity assertion.
Cryptographic origin trust is provided by **mTLS terminated at the internal ALB in verify
mode** — the gateway never sees a request whose client certificate was not validated by the
ALB, and the gateway additionally pins the cert subject/serial against an allowlist.

This is the recommended path because:

- No OWUI custom code (no Pipes, no plugins).
- Survives OWUI upgrades cleanly.
- Trust chain is anchored to a pinned client certificate, which the ITAR threat model
  treats as equivalent to a signed assertion within the OWUI⇄gateway hop.

### Path B — Pipe-injected JWT (custom OWUI code)

A small OWUI Pipe Function mints a short-lived JWT from the user object and injects it
as `X-Jarvis-User-Token` on every outbound request to the gateway. The gateway validates
it via the existing `OwuiSessionJwtValidator`.

Use this path only if a future audit specifically requires a per-request cryptographically
signed assertion beyond what mTLS provides. The Pipe code lives in the OWUI deployment, not
in this repository.

---

## 2. Open WebUI configuration (Path A)

### 2.1 Environment

Set these on the OWUI deployment (e.g. ECS task environment, Docker Compose, Helm values):

```bash
# Enables OWUI's native user-info header forwarding to external providers.
ENABLE_FORWARD_USER_INFO_HEADERS=true

# Required: OWUI must authenticate users via your corporate OIDC/SAML bridge so the
# email/id headers it forwards are sourced from your IdP, not OWUI's local accounts.
WEBUI_AUTH=true
ENABLE_OAUTH_SIGNUP=true
# ... your existing OAuth provider settings ...
```

### 2.2 Connection configuration

In OWUI's admin UI → **Settings → Connections**, add a new OpenAI-compatible connection:

| Field | Value |
|---|---|
| Provider name | `Jarvis Gateway` |
| Base URL | `https://jarvis-ai-gateway.internal/v1` |
| API Key | The service-to-service key from Secrets Manager (`Gateway:ServiceApiKey`) |
| Models | Leave empty (the gateway returns its allowlist via `/v1/models`) |

The service key is sent as `Authorization: Bearer <key>` by OWUI. The gateway accepts it
via the existing `ServiceApiKeyMiddleware` and treats `X-OpenWebUI-User-Email` (etc.) as
the user identity.

### 2.3 mTLS at the ALB

For the trusted-header path to be safe, the gateway's ingress must enforce mTLS:

1. Configure the gateway's internal ALB listener with `mutual_authentication.mode = "verify"`.
2. Provide the trust store (the internal CA that signs OWUI's client cert).
3. Issue a client certificate to the OWUI ECS task. Distribute it via Secrets Manager or
   ACM Private CA.
4. Configure OWUI's outbound HTTP client to present that certificate when calling the
   gateway. In ECS this is typically done via a sidecar proxy (Envoy) or by mounting the
   cert into the OWUI container and configuring `httpx`/`aiohttp` to use it.

The gateway expects ALB to forward these metadata headers in verify mode (it does NOT
parse a forwarded PEM body):

- `X-Amzn-Mtls-Clientcert-Subject`
- `X-Amzn-Mtls-Clientcert-Serial-Number`

Configure the gateway to pin one of these against an allowlist (see §3 below).

---

## 3. Gateway configuration

Required settings under `Gateway:IdentityBroker` (sourced from Secrets Manager / SSM, NOT
committed):

```bash
# Master switch
Gateway__IdentityBroker__Enabled=true

# Audit log subject hashing salt
Gateway__IdentityBroker__AuditSubjectSalt=<random 32+ byte string>

# Microsoft Graph (Entra commercial cloud)
Gateway__IdentityBroker__Graph__TenantId=<your Entra tenant id>
Gateway__IdentityBroker__Graph__ClientId=<gateway's Entra app registration id>
Gateway__IdentityBroker__Graph__ClientSecret=<client secret from Secrets Manager>

# Trusted-header validator (Path A)
Gateway__IdentityBroker__OwuiTrustedHeader__Enabled=true
Gateway__IdentityBroker__OwuiTrustedHeader__RequireMtlsSubjectPinning=true

# mTLS subject pinning — RequireSubjectCheck is default-on as of PR 4.  At least one
# accepted subject or serial MUST be configured or the gateway refuses readiness.
Gateway__IdentityBroker__Mtls__AcceptedClientCertSubjects__0=CN=jarvis-openwebui,OU=AI,O=YourOrg
# OR pin by serial number:
# Gateway__IdentityBroker__Mtls__AcceptedClientCertSerials__0=01ABCDEF...

# Disable the OWUI session JWT validator on this path (it would never match anyway)
Gateway__IdentityBroker__OwuiSessionJwt__Enabled=false
```

### Model authorization

ITAR-approved models MUST be authorized by Entra group object IDs once the broker is
enabled. Update each `Gateway:Models` entry:

```bash
Gateway__Models__0__Alias=jarvis-govcloud-itar
Gateway__Models__0__ItarApproved=true
Gateway__Models__0__AllowedGroupIds__0=<entra-group-object-id-guid>
```

`AllowedGroups` (display names) is ignored for any ITAR-approved model when the broker is
on — readiness fails closed otherwise. See plan §5 for the rationale.

---

## 4. `/v1/models` verification

The gateway defaults to `Gateway:IdentityBroker:ModelsEndpointRequiresUser=true`, meaning
`/v1/models` requires identity. Open WebUI v0.8.6+ calls `/v1/models` only in the context of
a user session — that's the path this default expects.

**Verify before flipping the broker on in production:**

1. In staging, with the broker enabled, watch the gateway's CloudWatch log stream while
   the OWUI admin opens the **Settings → Connections** page.
2. If you see `identity.token.missing` audit events firing for `/v1/models`, OWUI is
   calling that endpoint in a service context (no user session). In that case, switch the
   default:

```bash
Gateway__IdentityBroker__ModelsEndpointRequiresUser=false
```

With that flag off, `/v1/models` allows unauthenticated callers and returns only models
with no `AllowedGroupIds`/`RequiredGroupIds` — never ITAR models.

---

## 5. Smoke tests

After deploying the broker-enabled gateway and updating OWUI:

```bash
# 1. Liveness — should always be 200 regardless of mTLS / identity.
curl -k https://jarvis-ai-gateway.internal/healthz

# 2. Readiness — broker config + ITAR shape + mTLS pinning are all checked.
curl -k https://jarvis-ai-gateway.internal/readyz

# 3. Models listing without identity — should be 401 (Option A).
curl -k https://jarvis-ai-gateway.internal/v1/models

# 4. Models listing with the OWUI-style user headers — should be 200 and filtered.
curl -k https://jarvis-ai-gateway.internal/v1/models \
  -H "Authorization: Bearer $SERVICE_KEY" \
  -H "X-OpenWebUI-User-Email: alice@example.test" \
  -H "X-OpenWebUI-User-Id: 1234"

# 5. Chat completion — same headers, expect a 200 and an OpenAI-compatible response.
curl -k https://jarvis-ai-gateway.internal/v1/chat/completions \
  -H "Authorization: Bearer $SERVICE_KEY" \
  -H "X-OpenWebUI-User-Email: alice@example.test" \
  -H "Content-Type: application/json" \
  -d '{"model":"jarvis-govcloud-general","messages":[{"role":"user","content":"hello"}],"stream":false}'
```

For tests #4 and #5 you must hit the gateway from a network path that presents a valid
client cert pinned in `AcceptedClientCertSubjects` — that's the whole point of the trust
chain. From inside the ECS task, this means going through the ALB.

---

## 6. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `IDENTITY_TOKEN_MISSING` on `/v1/chat/completions` | OWUI is not sending the user-info headers | Set `ENABLE_FORWARD_USER_INFO_HEADERS=true` on OWUI, restart |
| `IDENTITY_USER_NOT_FOUND` | Graph could not resolve the email | Verify the email exists in the configured Entra tenant; check Graph permissions (§6 of the plan) |
| `IDENTITY_RESOLUTION_UNAVAILABLE` (503) | Graph call timed out or 5xx'd | Check network egress to `graph.microsoft.com` and `login.microsoftonline.com` from the gateway subnet |
| 401 with `IDENTITY_TOKEN_INVALID` and audit `mtls.subject_unexpected` | OWUI's client cert subject doesn't match the pinned list | Update `AcceptedClientCertSubjects` or re-issue the OWUI cert with the expected subject |
| Readiness fails with `OwuiTrustedHeader is enabled but Mtls.RequireSubjectCheck is false` | Misconfiguration | Set `Mtls.RequireSubjectCheck=true` and configure at least one accepted subject |
| `/v1/models` returns 401 only during OWUI provider setup | OWUI calling `/v1/models` without user session | Set `ModelsEndpointRequiresUser=false` |

---

## 7. Migration from the legacy JwtBearer path

As of PR 3 the broker is **on by default** (`Gateway:IdentityBroker:Enabled=true`).
Deployments that need to stay on the legacy ASP.NET Core JwtBearer flow during migration
can opt out explicitly:

```bash
Gateway__IdentityBroker__Enabled=false
```

The legacy code path is intentionally retained so the rollback is a config change, not a
code revert.

To migrate forward from the legacy path:

1. **Staging:** confirm `Gateway:IdentityBroker:Enabled=true` and
   `Mtls:RequireSubjectCheck=true` (both defaults as of PR 3 and PR 4 respectively), set
   `OwuiTrustedHeader:Enabled=true`, configure at least one
   `Mtls:AcceptedClientCertSubjects` entry, and supply Graph credentials and
   `AuditSubjectSalt`. Run the smoke tests in §5 against a real Entra tenant.
2. **Production:** repeat once staging is green for at least one full release cycle.
3. **Cleanup (follow-up PR):** remove the JwtBearer registration entirely and retire the
   static `X-Jarvis-Gateway-Key` belt-and-suspenders once the mTLS path is verified.

---

## 8. Path B implementation note (Pipe-injected JWT)

If you ever switch to Path B, the OWUI-side artifact is a Pipe Function that:

1. Reads the inbound user from the OWUI request context.
2. Mints a short-lived (~60s) JWT signed with a key shared with the gateway, with at
   minimum `email`, `iat`, `exp`.
3. Adds it to the outbound request as `X-Jarvis-User-Token: <jwt>`.

Gateway side configuration switches from `OwuiTrustedHeader` to `OwuiSessionJwt`:

```bash
Gateway__IdentityBroker__OwuiTrustedHeader__Enabled=false
Gateway__IdentityBroker__OwuiSessionJwt__Enabled=true
Gateway__IdentityBroker__OwuiSessionJwt__SigningKey=<shared with the Pipe>
Gateway__IdentityBroker__OwuiSessionJwt__MaxAssertionAgeSeconds=60
```

The Pipe code itself is not in this repository. If/when this path is adopted, the Pipe
lives in the OWUI deployment alongside other Functions/Pipes and is owned by the OWUI
team.

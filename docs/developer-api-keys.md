# Developer API Keys

Phase 0 adds a **developer/IDE credential** that is separate from the Open WebUI
service-to-service key. It lets an individual developer (or an OpenAI-compatible tool such as
Continue/Cline) call the gateway with a standard bearer token while remaining subject to **all**
existing policy, ITAR, group, redaction, and audit controls.

## Two distinct credentials — do not mix them

| Credential | Header | Who | Authorizes as |
|---|---|---|---|
| Service-to-service key | `X-Jarvis-Gateway-Key: <key>` | Open WebUI / internal services | the service principal (`ServiceApiKeyGroups`) |
| **Developer API key** | `Authorization: Bearer jrvs_<token>` | an individual developer / IDE | **the owning user** (live Entra groups) |

- The Open WebUI service key must **never** be handed to developers or pasted into an IDE.
- A developer key authenticates *as its owner*: the gateway resolves the owner's **current** Entra
  group membership at request time, so the key cannot grant more than the owner has today and is
  never an authorization escalation path. ITAR/group/model rules apply unchanged.

## Behavior

- Enabled by `Gateway:DeveloperAuth:Enabled=true` (off by default).
- Only bearers beginning with the configured prefix (`jrvs_`) are treated as developer keys;
  legacy JWT bearers and the service key are untouched.
- Invalid, unknown, expired, revoked, or malformed keys **fail closed** with a single generic
  `401 INVALID_API_KEY` (the response never reveals which condition failed or whether a key
  exists).
- If the owner's directory lookup is unavailable, the request fails closed
  (`503 IDENTITY_RESOLUTION_UNAVAILABLE`); an unknown owner returns `403 IDENTITY_USER_NOT_FOUND`.
- Per-key rate limiting applies (keyed on the key id); per-user limiting applies otherwise.
- Audit records the **key id** and a non-reversible fingerprint — never the raw key.

## Storage & hashing

- Only an **HMAC-SHA256(pepper, rawKey)** hash is stored — never the raw key.
- `Gateway:DeveloperAuth:HashPepper` is a required secret when developer auth is enabled
  (readiness fails closed via `/healthz/ready` if it is missing).
- Phase 0 backing store is configuration (`Gateway:DeveloperAuth:Keys`), sourced from a secret
  store (e.g. AWS Secrets Manager). The `IDeveloperApiKeyStore` seam allows a
  DynamoDB/RDS/SSM-backed store later without touching authentication.

## Provisioning a key (Phase 0, manual)

1. Generate a random token with the prefix:
   ```bash
   RAW="jrvs_$(openssl rand -hex 24)"
   ```
2. Compute its stored hash with the configured pepper (lowercase hex):
   ```bash
   printf '%s' "$RAW" | openssl dgst -sha256 -hmac "$DEVELOPER_AUTH_PEPPER" -hex | awk '{print $2}'
   ```
3. Store the **hash** + metadata in `Gateway:DeveloperAuth:Keys` (secret store), e.g.:
   ```json
   {
     "Gateway": {
       "DeveloperAuth": {
         "Enabled": true,
         "HashPepper": "REPLACE_WITH_PEPPER_SECRET",
         "Keys": [
           {
             "KeyId": "dev-jane-vscode",
             "KeyHash": "<hex hash from step 2>",
             "OwnerSubject": "jane.doe@agency.gov",
             "OwnerEmail": "jane.doe@agency.gov",
             "DisplayName": "Jane Doe — VS Code",
             "CreatedUtc": "2026-06-24T00:00:00Z",
             "ExpiresUtc": "2026-12-31T00:00:00Z",
             "Revoked": false,
             "Scopes": [ "chat" ],
             "ModelAllowlist": [],
             "ClientName": "vscode"
           }
         ]
       }
     }
   }
   ```
4. Give the **raw** token to the developer once. The gateway never stores or echoes it.

- **Revoke:** set `Revoked: true` (or remove the entry).
- **Expire:** set `ExpiresUtc` in the past (enforced on every request).
- **Restrict models:** populate `ModelAllowlist` (alias/id). This can only *further* restrict —
  group/ITAR rules still apply on top.

A self-service issuance endpoint (return raw key once at creation) is intentionally **not** part
of Phase 0; it is a later addition once a persistent `IDeveloperApiKeyStore` is in place.

## Security notes

- Never log raw keys; the gateway logs only key id + fingerprint.
- Keys inherit the owner's ITAR/group authorization — they cannot reach a model the owner cannot.
- Keep the pepper in a secret store; rotating it invalidates all existing hashes (re-provision).

namespace Jarvis.AiGateway.Options;

/// <summary>
/// Developer (IDE/client) API-key authentication.  This is a SEPARATE credential path from the
/// service-to-service key (<see cref="GatewayOptions.ServiceApiKey"/>): a developer key
/// authenticates an individual user via <c>Authorization: Bearer jrvs_…</c> and is subject to the
/// same policy/ITAR/group authorization as a JWT/identity-broker user.
///
/// <para>Off by default.  When enabled, <see cref="HashPepper"/> is required (readiness fails
/// closed otherwise).  Keys are provisioned out-of-band (Phase 0 backing store is configuration):
/// only the HMAC hash + metadata are stored here — never the raw key.</para>
/// </summary>
public sealed class DeveloperAuthOptions
{
    public bool Enabled { get; set; }

    // Required token prefix. The middleware ignores any bearer that does not start with this, so
    // legacy JWT bearers are never consumed by the developer path.
    public string KeyPrefix { get; set; } = "jrvs_";

    // Server-side pepper for HMAC-SHA256 key hashing. Sourced from a secret store — never committed.
    public string? HashPepper { get; set; }

    public List<DeveloperApiKeyEntry> Keys { get; set; } = [];
}

/// <summary>
/// A provisioned developer API key.  <see cref="KeyHash"/> is HMAC-SHA256(pepper, rawKey) as
/// lowercase hex — the raw key is never stored.
/// </summary>
public sealed class DeveloperApiKeyEntry
{
    public string KeyId { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string OwnerSubject { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset? CreatedUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public bool Revoked { get; set; }

    // Forward-looking capability scopes (e.g. "chat"). Phase 0 only exposes chat; stored for later.
    public List<string> Scopes { get; set; } = [];

    // Optional per-key model allowlist (by alias/id). Empty = no key-level model restriction
    // (gateway policy/group authorization still applies).
    public List<string> ModelAllowlist { get; set; } = [];

    public string? ClientName { get; set; }
}

/// <summary>Internal API discoverability (OpenAPI). Off by default; enable only on internal builds.</summary>
public sealed class DiscoveryOptions
{
    public bool OpenApiEnabled { get; set; }
}

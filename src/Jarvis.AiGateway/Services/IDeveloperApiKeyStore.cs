namespace Jarvis.AiGateway.Services;

/// <summary>
/// Resolved developer API key record (metadata only — never the raw key or its hash beyond lookup).
/// </summary>
public sealed record DeveloperApiKey(
    string KeyId,
    string OwnerSubject,
    string OwnerEmail,
    string DisplayName,
    DateTimeOffset? ExpiresUtc,
    bool Revoked,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> ModelAllowlist,
    string? ClientName);

/// <summary>
/// Lookup seam for developer API keys.  Keyed on the HMAC hash of the presented key (never the
/// raw key).  Implementations are replaceable (Phase 0 ships a configuration-backed store; a
/// DynamoDB / Secrets Manager / SSM / RDS store can be substituted later without touching auth).
/// </summary>
public interface IDeveloperApiKeyStore
{
    /// <summary>Returns the key record for the given HMAC hash, or null when no key matches.</summary>
    Task<DeveloperApiKey?> FindByHashAsync(string keyHash, CancellationToken cancellationToken);
}

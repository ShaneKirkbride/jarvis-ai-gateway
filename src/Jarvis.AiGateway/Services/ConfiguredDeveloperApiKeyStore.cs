using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Phase 0 developer API-key store backed by configuration (<c>Gateway:DeveloperAuth:Keys</c>),
/// sourced from a secret store (e.g. AWS Secrets Manager) at deploy time.  Stores only HMAC
/// hashes + metadata — never raw keys.  Replace with a DynamoDB/RDS-backed
/// <see cref="IDeveloperApiKeyStore"/> for self-service key management.
/// </summary>
public sealed class ConfiguredDeveloperApiKeyStore : IDeveloperApiKeyStore
{
    private readonly IReadOnlyDictionary<string, DeveloperApiKey> _byHash;

    public ConfiguredDeveloperApiKeyStore(IOptions<GatewayOptions> options)
    {
        var entries = options.Value.DeveloperAuth.Keys;
        var map = new Dictionary<string, DeveloperApiKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.KeyHash) || string.IsNullOrWhiteSpace(entry.KeyId))
            {
                // Skip malformed entries rather than fail startup; readiness validation reports them.
                continue;
            }

            map[entry.KeyHash] = new DeveloperApiKey(
                entry.KeyId,
                entry.OwnerSubject,
                entry.OwnerEmail,
                entry.DisplayName,
                entry.ExpiresUtc,
                entry.Revoked,
                entry.Scopes.AsReadOnly(),
                entry.ModelAllowlist.AsReadOnly(),
                entry.ClientName);
        }

        _byHash = map;
    }

    public Task<DeveloperApiKey?> FindByHashAsync(string keyHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyHash))
        {
            return Task.FromResult<DeveloperApiKey?>(null);
        }

        return Task.FromResult(_byHash.TryGetValue(keyHash, out var key) ? key : null);
    }
}

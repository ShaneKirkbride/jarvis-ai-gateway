using System.Security.Cryptography;
using System.Text;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Hashes presented developer API keys for storage lookup and produces a non-reversible
/// fingerprint for audit.  Uses HMAC-SHA256 with a server-side pepper so a leak of the stored
/// hashes does not enable offline brute force without also obtaining the pepper.  Raw keys are
/// never persisted or logged.
/// </summary>
public interface IDeveloperApiKeyHasher
{
    /// <summary>Deterministic HMAC-SHA256(pepper, key) as lowercase hex — used for store lookup.</summary>
    string Hash(string presentedKey);

    /// <summary>Short, non-reversible identifier safe to log for a (possibly invalid) key.</summary>
    string Fingerprint(string presentedKey);
}

public sealed class DeveloperApiKeyHasher : IDeveloperApiKeyHasher
{
    private readonly byte[] _pepper;

    public DeveloperApiKeyHasher(IOptions<GatewayOptions> options)
    {
        var pepper = options.Value.DeveloperAuth.HashPepper;
        // The pepper is required when developer auth is enabled; readiness fails closed if it is
        // missing. An empty pepper here would be a programming/config error — fail loudly on use.
        _pepper = string.IsNullOrEmpty(pepper) ? [] : Encoding.UTF8.GetBytes(pepper);
    }

    public string Hash(string presentedKey)
    {
        if (_pepper.Length == 0)
        {
            throw new InvalidOperationException("Developer API key pepper is not configured.");
        }

        var bytes = HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(presentedKey ?? string.Empty));
        return Convert.ToHexStringLower(bytes);
    }

    public string Fingerprint(string presentedKey)
    {
        // Independent of the lookup hash and of the pepper: a plain SHA-256 prefix is enough to
        // correlate repeated bad keys in audit without revealing the key or the lookup hash.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey ?? string.Empty));
        return Convert.ToHexStringLower(bytes)[..12];
    }
}

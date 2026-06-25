using System.Security.Claims;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

/// <summary>Claim types stamped on a developer-API-key principal.</summary>
public static class DeveloperApiKeyClaims
{
    public const string AuthenticationScheme = "JarvisDeveloperApiKey";

    // Same claim TYPE the service-key path uses, so downstream code distinguishing auth modes
    // reads one claim regardless of credential. Value differs ("developer_api_key").
    public const string AuthTypeClaim = ServiceApiKeyMiddleware.AuthTypeClaimType;
    public const string AuthTypeValue = "developer_api_key";

    public const string KeyIdClaim = "jarvis:apikey_id";
    public const string ModelAllowlistClaim = "jarvis:apikey_model_allowlist";
}

public enum DeveloperApiKeyOutcome
{
    Authenticated,
    Malformed,
    InvalidKey,
    Expired,
    Revoked,
    OwnerNotFound,
    ResolutionUnavailable
}

public sealed record DeveloperApiKeyAuthResult(
    DeveloperApiKeyOutcome Outcome,
    ClaimsPrincipal? Principal,
    string? KeyId,
    string Fingerprint)
{
    public bool IsAuthenticated => Outcome == DeveloperApiKeyOutcome.Authenticated;

    public static DeveloperApiKeyAuthResult Fail(DeveloperApiKeyOutcome outcome, string fingerprint, string? keyId = null) =>
        new(outcome, null, keyId, fingerprint);

    public static DeveloperApiKeyAuthResult Success(ClaimsPrincipal principal, string keyId, string fingerprint) =>
        new(DeveloperApiKeyOutcome.Authenticated, principal, keyId, fingerprint);
}

/// <summary>
/// Validates a presented developer API key and, on success, produces a
/// <see cref="ClaimsPrincipal"/> that is authorization-equivalent to a JWT / identity-broker user
/// (same <c>sub</c>/<c>email</c> and Entra group claims).  Group membership is resolved LIVE via
/// <see cref="IGraphGroupResolver"/> — never snapshotted — so a developer key can never grant more
/// than the owner's current membership.  All non-success outcomes fail closed.
/// </summary>
public interface IDeveloperApiKeyAuthenticator
{
    Task<DeveloperApiKeyAuthResult> AuthenticateAsync(string presentedKey, CancellationToken cancellationToken);
}

public sealed class DeveloperApiKeyAuthenticator(
    IDeveloperApiKeyStore store,
    IDeveloperApiKeyHasher hasher,
    IOptions<GatewayOptions> options,
    TimeProvider timeProvider,
    IGraphGroupResolver? groupResolver = null) : IDeveloperApiKeyAuthenticator
{
    private readonly DeveloperAuthOptions _options = options.Value.DeveloperAuth;

    public async Task<DeveloperApiKeyAuthResult> AuthenticateAsync(string presentedKey, CancellationToken cancellationToken)
    {
        var fingerprint = hasher.Fingerprint(presentedKey ?? string.Empty);

        if (string.IsNullOrWhiteSpace(presentedKey) || !presentedKey.StartsWith(_options.KeyPrefix, StringComparison.Ordinal))
        {
            return DeveloperApiKeyAuthResult.Fail(DeveloperApiKeyOutcome.Malformed, fingerprint);
        }

        var key = await store.FindByHashAsync(hasher.Hash(presentedKey), cancellationToken);
        if (key is null)
        {
            return DeveloperApiKeyAuthResult.Fail(DeveloperApiKeyOutcome.InvalidKey, fingerprint);
        }

        if (key.Revoked)
        {
            return DeveloperApiKeyAuthResult.Fail(DeveloperApiKeyOutcome.Revoked, fingerprint, key.KeyId);
        }

        if (key.ExpiresUtc is { } expiry && expiry <= timeProvider.GetUtcNow())
        {
            return DeveloperApiKeyAuthResult.Fail(DeveloperApiKeyOutcome.Expired, fingerprint, key.KeyId);
        }

        // Resolve the owner's CURRENT groups. No resolver configured → identity-only principal
        // (works for models with no required group; group-gated/ITAR models deny — fail closed).
        IReadOnlySet<DirectoryGroupRef> groups = new HashSet<DirectoryGroupRef>();
        if (groupResolver is not null)
        {
            var canonicalSubject = string.IsNullOrWhiteSpace(key.OwnerEmail) ? key.OwnerSubject : key.OwnerEmail;
            var lookup = await groupResolver.ResolveAsync(canonicalSubject, cancellationToken);
            if (!lookup.IsSuccess)
            {
                var outcome = lookup.FailureReason == AssertionFailureReason.GraphUserNotFound
                    ? DeveloperApiKeyOutcome.OwnerNotFound
                    : DeveloperApiKeyOutcome.ResolutionUnavailable;
                return DeveloperApiKeyAuthResult.Fail(outcome, fingerprint, key.KeyId);
            }

            groups = lookup.Groups;
        }

        return DeveloperApiKeyAuthResult.Success(BuildPrincipal(key, groups), key.KeyId, fingerprint);
    }

    private static ClaimsPrincipal BuildPrincipal(DeveloperApiKey key, IReadOnlySet<DirectoryGroupRef> groups)
    {
        var claims = new List<Claim>
        {
            new("sub", key.OwnerSubject),
            new(ClaimTypes.NameIdentifier, key.OwnerSubject),
            new(DeveloperApiKeyClaims.AuthTypeClaim, DeveloperApiKeyClaims.AuthTypeValue),
            new(DeveloperApiKeyClaims.KeyIdClaim, key.KeyId)
        };

        if (!string.IsNullOrWhiteSpace(key.OwnerEmail))
        {
            claims.Add(new Claim("email", key.OwnerEmail));
            claims.Add(new Claim(ClaimTypes.Email, key.OwnerEmail));
        }

        if (key.ModelAllowlist.Count > 0)
        {
            claims.Add(new Claim(DeveloperApiKeyClaims.ModelAllowlistClaim, string.Join(",", key.ModelAllowlist)));
        }

        // Group claims are emitted identically to the identity broker so the policy engine and
        // UserContextFactory authorize a developer-key user exactly like a JWT/broker user.
        foreach (var group in groups)
        {
            claims.Add(new Claim(IdentityBrokerMiddleware.GroupIdClaim, group.Id));
            claims.Add(new Claim(ClaimTypes.Role, group.Id));
            if (!string.IsNullOrWhiteSpace(group.DisplayName))
            {
                claims.Add(new Claim(IdentityBrokerMiddleware.GroupNameClaim, group.DisplayName));
            }
        }

        var identity = new ClaimsIdentity(claims, DeveloperApiKeyClaims.AuthenticationScheme, "sub", ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}

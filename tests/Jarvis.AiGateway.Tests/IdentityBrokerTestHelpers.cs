using System.Text;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// Helpers shared across identity-broker unit tests.  Centralised so test JWT minting,
/// fake clock control, and fake Graph behaviour stay consistent and DRY across the
/// IdentityBroker-related test files.
/// </summary>
internal static class IdentityBrokerTestHelpers
{
    public const string DefaultSigningKey = "test-signing-key-32-bytes-min-length-padding-padding";

    public static string MintOwuiJwt(
        string signingKey = DefaultSigningKey,
        string? email = "user@example.test",
        string? upn = null,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expires = null,
        string? issuer = null,
        string? audience = null,
        string algorithm = "HS256")
    {
        var iat = issuedAt ?? DateTimeOffset.UtcNow;
        var exp = expires ?? iat.AddMinutes(15);

        var claims = new Dictionary<string, object>
        {
            ["iat"] = iat.ToUnixTimeSeconds(),
            ["exp"] = exp.ToUnixTimeSeconds()
        };
        if (email is not null) claims["email"] = email;
        if (upn is not null) claims["upn"] = upn;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        var creds = new SigningCredentials(key, algorithm);

        var descriptor = new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = creds,
            IssuedAt = iat.UtcDateTime,
            Expires = exp.UtcDateTime,
            Issuer = issuer,
            Audience = audience
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static GatewayOptions DefaultBrokerOptions(string? signingKey = DefaultSigningKey, string? salt = "test-salt")
    {
        return new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions
            {
                Enabled = true,
                AuditSubjectSalt = salt,
                ClockSkewSeconds = 60,
                MaxAssertionAgeSeconds = 900,
                OwuiSessionJwt = new OwuiSessionJwtOptions
                {
                    Enabled = true,
                    SigningKey = signingKey
                },
                Graph = new GraphOptions
                {
                    TenantId = "tenant",
                    ClientId = "client",
                    ClientSecret = "secret",
                    CacheSeconds = 300,
                    NegativeCacheSeconds = 30,
                    TimeoutSeconds = 5
                }
            }
        };
    }
}

internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

internal sealed class FakeGraphGroupQueryExecutor : IGraphGroupQueryExecutor
{
    public Func<string, CancellationToken, Task<GraphQueryResult>> Behavior { get; set; } =
        (_, _) => Task.FromResult(new GraphQueryResult(true, Array.Empty<DirectoryGroupRef>(), "oid-default", AssertionFailureReason.None, null));

    public int CallCount;

    public Task<GraphQueryResult> ExecuteAsync(string canonicalSubject, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref CallCount);
        return Behavior(canonicalSubject, cancellationToken);
    }
}

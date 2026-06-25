using System.Security.Claims;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Jarvis.AiGateway.Services;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

public sealed class DeveloperApiKeyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

    // ── Hasher ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hasher_is_deterministic_and_pepper_sensitive()
    {
        var a = new DeveloperApiKeyHasher(Opts(pepper: "pepper-one"));
        var b = new DeveloperApiKeyHasher(Opts(pepper: "pepper-two"));

        Assert.Equal(a.Hash("jrvs_abc"), a.Hash("jrvs_abc"));
        Assert.NotEqual(a.Hash("jrvs_abc"), a.Hash("jrvs_xyz"));
        Assert.NotEqual(a.Hash("jrvs_abc"), b.Hash("jrvs_abc"));
        Assert.Equal(12, a.Fingerprint("jrvs_abc").Length);
        Assert.NotEqual("jrvs_abc", a.Hash("jrvs_abc")); // never the raw key
    }

    [Fact]
    public void Hasher_throws_when_pepper_missing()
    {
        var hasher = new DeveloperApiKeyHasher(Opts(pepper: ""));
        Assert.Throws<InvalidOperationException>(() => hasher.Hash("jrvs_abc"));
    }

    // ── Store ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Configured_store_finds_by_hash_and_skips_malformed_entries()
    {
        var options = MsOptions.Create(new GatewayOptions
        {
            DeveloperAuth = new DeveloperAuthOptions
            {
                Enabled = true,
                HashPepper = "p",
                Keys =
                [
                    new DeveloperApiKeyEntry { KeyId = "k1", KeyHash = "hash-1", OwnerSubject = "dev@example.test" },
                    new DeveloperApiKeyEntry { KeyId = "", KeyHash = "hash-2" } // malformed: skipped
                ]
            }
        });
        var store = new ConfiguredDeveloperApiKeyStore(options);

        Assert.Equal("k1", (await store.FindByHashAsync("hash-1", default))!.KeyId);
        Assert.Null(await store.FindByHashAsync("hash-2", default));
        Assert.Null(await store.FindByHashAsync("nope", default));
        Assert.Null(await store.FindByHashAsync("", default));
    }

    // ── Authenticator ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_key_authenticates_with_broker_equivalent_claims()
    {
        const string raw = "jrvs_valid";
        var hasher = new DeveloperApiKeyHasher(Opts());
        var store = new FakeStore(hasher.Hash(raw), Key());
        var resolver = new FakeResolver(GraphLookupResult.Success(
            new HashSet<DirectoryGroupRef> { new("gid-1", "AI-General-Users") }, "oid-1", wasCached: false));
        var auth = new DeveloperApiKeyAuthenticator(store, hasher, Opts(), new FixedClock(Now), resolver);

        var result = await auth.AuthenticateAsync(raw, default);

        Assert.True(result.IsAuthenticated);
        Assert.Equal("k1", result.KeyId);
        var p = result.Principal!;
        Assert.True(p.Identity!.IsAuthenticated);
        Assert.Equal("dev-subject", p.FindFirstValue("sub"));
        Assert.Equal("dev@example.test", p.FindFirstValue("email"));
        Assert.Equal("developer_api_key", p.FindFirstValue(DeveloperApiKeyClaims.AuthTypeClaim));
        Assert.Equal("k1", p.FindFirstValue(DeveloperApiKeyClaims.KeyIdClaim));
        // Group object id is emitted as both jarvis:group_id and Role (broker-equivalent shape).
        Assert.Equal("gid-1", p.FindFirstValue(IdentityBrokerMiddleware.GroupIdClaim));
        Assert.Equal("gid-1", p.FindFirstValue(ClaimTypes.Role));
        Assert.Equal("AI-General-Users", p.FindFirstValue(IdentityBrokerMiddleware.GroupNameClaim));
    }

    [Fact]
    public async Task Unknown_expired_revoked_and_malformed_keys_fail_closed()
    {
        var hasher = new DeveloperApiKeyHasher(Opts());

        // Unknown (store returns null)
        var unknown = await Authenticator(hasher, new FakeStore("other", Key())).AuthenticateAsync("jrvs_unknown", default);
        Assert.Equal(DeveloperApiKeyOutcome.InvalidKey, unknown.Outcome);

        // Malformed (wrong prefix)
        var malformed = await Authenticator(hasher, new FakeStore("x", Key())).AuthenticateAsync("sk-not-ours", default);
        Assert.Equal(DeveloperApiKeyOutcome.Malformed, malformed.Outcome);

        // Malformed (empty)
        var empty = await Authenticator(hasher, new FakeStore("x", Key())).AuthenticateAsync("", default);
        Assert.Equal(DeveloperApiKeyOutcome.Malformed, empty.Outcome);

        // Revoked
        const string revokedRaw = "jrvs_revoked";
        var revoked = await Authenticator(hasher, new FakeStore(hasher.Hash(revokedRaw), Key(revoked: true)))
            .AuthenticateAsync(revokedRaw, default);
        Assert.Equal(DeveloperApiKeyOutcome.Revoked, revoked.Outcome);

        // Expired
        const string expiredRaw = "jrvs_expired";
        var expired = await Authenticator(hasher, new FakeStore(hasher.Hash(expiredRaw), Key(expires: Now.AddMinutes(-1))))
            .AuthenticateAsync(expiredRaw, default);
        Assert.Equal(DeveloperApiKeyOutcome.Expired, expired.Outcome);

        // All non-success → no principal
        Assert.All(new[] { unknown, malformed, revoked, expired }, r => Assert.Null(r.Principal));
    }

    [Fact]
    public async Task Resolver_failure_fails_closed_and_user_not_found_is_distinct()
    {
        const string raw = "jrvs_valid";
        var hasher = new DeveloperApiKeyHasher(Opts());
        var store = new FakeStore(hasher.Hash(raw), Key());

        var unavailable = await new DeveloperApiKeyAuthenticator(store, hasher, Opts(), new FixedClock(Now),
            new FakeResolver(GraphLookupResult.Failure(AssertionFailureReason.GraphLookupFailed, null, false)))
            .AuthenticateAsync(raw, default);
        Assert.Equal(DeveloperApiKeyOutcome.ResolutionUnavailable, unavailable.Outcome);

        var notFound = await new DeveloperApiKeyAuthenticator(store, hasher, Opts(), new FixedClock(Now),
            new FakeResolver(GraphLookupResult.Failure(AssertionFailureReason.GraphUserNotFound, null, false)))
            .AuthenticateAsync(raw, default);
        Assert.Equal(DeveloperApiKeyOutcome.OwnerNotFound, notFound.Outcome);
    }

    [Fact]
    public async Task No_resolver_authenticates_with_no_groups()
    {
        const string raw = "jrvs_valid";
        var hasher = new DeveloperApiKeyHasher(Opts());
        var auth = new DeveloperApiKeyAuthenticator(new FakeStore(hasher.Hash(raw), Key()), hasher, Opts(), new FixedClock(Now));

        var result = await auth.AuthenticateAsync(raw, default);

        Assert.True(result.IsAuthenticated);
        Assert.Empty(result.Principal!.FindAll(ClaimTypes.Role));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static Microsoft.Extensions.Options.IOptions<GatewayOptions> Opts(string pepper = "test-pepper", string prefix = "jrvs_") =>
        MsOptions.Create(new GatewayOptions { DeveloperAuth = new DeveloperAuthOptions { Enabled = true, HashPepper = pepper, KeyPrefix = prefix } });

    private static DeveloperApiKeyAuthenticator Authenticator(DeveloperApiKeyHasher hasher, IDeveloperApiKeyStore store) =>
        new(store, hasher, Opts(), new FixedClock(Now));

    private static DeveloperApiKey Key(bool revoked = false, DateTimeOffset? expires = null) =>
        new("k1", "dev-subject", "dev@example.test", "Dev Key", expires, revoked, [], [], "vscode");

    private sealed class FakeStore(string hash, DeveloperApiKey? key) : IDeveloperApiKeyStore
    {
        public Task<DeveloperApiKey?> FindByHashAsync(string keyHash, CancellationToken cancellationToken) =>
            Task.FromResult(string.Equals(keyHash, hash, StringComparison.OrdinalIgnoreCase) ? key : null);
    }

    private sealed class FakeResolver(GraphLookupResult result) : IGraphGroupResolver
    {
        public Task<GraphLookupResult> ResolveAsync(string canonicalSubject, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

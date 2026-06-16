using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class OwuiSessionJwtValidatorTests
{
    // Anchor on the actual system clock so the JsonWebTokenHandler's internal lifetime
    // checks (which consult DateTime.UtcNow directly) line up with the TestTimeProvider
    // injected into the validator.  Fixed test dates drift away from the system clock and
    // produce confusing "expired" failures on long-running CI machines.
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Valid_token_returns_normalized_email_and_upn()
    {
        var validator = CreateValidator(Now, out _);
        var jwt = IdentityBrokerTestHelpers.MintOwuiJwt(
            email: "  USER@Example.Test  ",
            upn: "USER@example.test",
            issuedAt: Now,
            expires: Now.AddMinutes(10));

        var result = await validator.ValidateAsync(MakeInput(jwt), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(OwuiSessionJwtValidator.Kind, result.AssertionKind);
        Assert.Equal("user@example.test", result.Email);
        Assert.Equal("user@example.test", result.Upn);
        Assert.NotNull(result.IssuedAt);
        Assert.NotNull(result.ExpiresAt);
    }

    [Fact]
    public async Task Bad_signature_returns_token_invalid()
    {
        var validator = CreateValidator(Now, out _);
        var jwt = IdentityBrokerTestHelpers.MintOwuiJwt(signingKey: "different-signing-key-with-padding-and-padding", issuedAt: Now);

        var result = await validator.ValidateAsync(MakeInput(jwt), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenInvalid, result.FailureReason);
    }

    [Fact]
    public async Task Expired_token_returns_token_expired()
    {
        var validator = CreateValidator(Now, out _);
        var jwt = IdentityBrokerTestHelpers.MintOwuiJwt(issuedAt: Now.AddMinutes(-30), expires: Now.AddMinutes(-10));

        var result = await validator.ValidateAsync(MakeInput(jwt), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.True(result.FailureReason == AssertionFailureReason.TokenExpired,
            $"Expected TokenExpired but got {result.FailureReason} with hint '{result.DiagnosticHint}'.");
    }

    [Fact]
    public async Task Iat_older_than_max_age_returns_token_too_old()
    {
        var validator = CreateValidator(Now, out _, options =>
        {
            options.IdentityBroker.MaxAssertionAgeSeconds = 60;
        });
        // exp is in the future, iat is well past max age
        var jwt = IdentityBrokerTestHelpers.MintOwuiJwt(issuedAt: Now.AddMinutes(-30), expires: Now.AddHours(1));

        var result = await validator.ValidateAsync(MakeInput(jwt), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenTooOld, result.FailureReason);
    }

    [Fact]
    public async Task Iat_within_max_age_with_skew_passes()
    {
        var validator = CreateValidator(Now, out _, options =>
        {
            options.IdentityBroker.MaxAssertionAgeSeconds = 60;
            options.IdentityBroker.ClockSkewSeconds = 30;
        });
        // iat 85s ago — within 60s + 30s skew
        var jwt = IdentityBrokerTestHelpers.MintOwuiJwt(issuedAt: Now.AddSeconds(-85), expires: Now.AddHours(1));

        var result = await validator.ValidateAsync(MakeInput(jwt), CancellationToken.None);

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Missing_email_and_upn_returns_weak_identity()
    {
        var validator = CreateValidator(Now, out _);
        var jwt = IdentityBrokerTestHelpers.MintOwuiJwt(email: null, upn: null, issuedAt: Now);

        var result = await validator.ValidateAsync(MakeInput(jwt), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenWeakIdentity, result.FailureReason);
    }

    [Fact]
    public async Task Issuer_or_audience_mismatch_returns_token_invalid()
    {
        var validator = CreateValidator(Now, out _, options =>
        {
            options.IdentityBroker.OwuiSessionJwt.Issuer = "https://expected-issuer";
            options.IdentityBroker.OwuiSessionJwt.Audience = "expected-audience";
        });
        var jwt = IdentityBrokerTestHelpers.MintOwuiJwt(issuer: "https://wrong-issuer", audience: "expected-audience", issuedAt: Now);

        var result = await validator.ValidateAsync(MakeInput(jwt), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenInvalid, result.FailureReason);
    }

    [Fact]
    public void Can_handle_returns_false_for_non_three_segment_token()
    {
        var validator = CreateValidator(Now, out _);
        Assert.False(validator.CanHandle(MakeInput("not.two-segments")));
        Assert.False(validator.CanHandle(MakeInput("")));
        Assert.False(validator.CanHandle(MakeInput("a.b.c.d")));
    }

    [Fact]
    public void Can_handle_returns_false_when_validator_disabled()
    {
        var validator = CreateValidator(Now, out _, options =>
        {
            options.IdentityBroker.OwuiSessionJwt.Enabled = false;
        });
        var jwt = IdentityBrokerTestHelpers.MintOwuiJwt(issuedAt: Now);
        Assert.False(validator.CanHandle(MakeInput(jwt)));
    }

    [Fact]
    public async Task Configured_without_signing_key_fails_closed()
    {
        var validator = CreateValidator(Now, out _, options =>
        {
            options.IdentityBroker.OwuiSessionJwt.SigningKey = null;
        });
        var jwt = IdentityBrokerTestHelpers.MintOwuiJwt(issuedAt: Now);

        var result = await validator.ValidateAsync(MakeInput(jwt), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenInvalid, result.FailureReason);
    }

    [Fact]
    public async Task Malformed_token_fails_closed_without_throwing()
    {
        var validator = CreateValidator(Now, out _);

        var result = await validator.ValidateAsync(MakeInput("not-a-real-jwt"), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenInvalid, result.FailureReason);
    }

    [Fact]
    public async Task Per_validator_max_age_overrides_global()
    {
        var validator = CreateValidator(Now, out _, options =>
        {
            options.IdentityBroker.MaxAssertionAgeSeconds = 600;        // global generous
            options.IdentityBroker.OwuiSessionJwt.MaxAssertionAgeSeconds = 30; // strict per-validator
        });
        var jwt = IdentityBrokerTestHelpers.MintOwuiJwt(issuedAt: Now.AddSeconds(-200), expires: Now.AddHours(1));

        var result = await validator.ValidateAsync(MakeInput(jwt), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(AssertionFailureReason.TokenTooOld, result.FailureReason);
    }

    private static IdentityAssertionInput MakeInput(string? rawAssertion) =>
        new(rawAssertion, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    private static OwuiSessionJwtValidator CreateValidator(DateTimeOffset now, out TestTimeProvider time, Action<GatewayOptions>? configure = null)
    {
        var options = IdentityBrokerTestHelpers.DefaultBrokerOptions();
        configure?.Invoke(options);
        time = new TestTimeProvider(now);
        return new OwuiSessionJwtValidator(MsOptions.Create(options), time, NullLogger<OwuiSessionJwtValidator>.Instance);
    }
}

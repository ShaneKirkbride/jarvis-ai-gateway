using System.Text;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Validator for the current Open WebUI session JWT (HS256-signed against
/// <c>WEBUI_SECRET_KEY</c>).
/// <para>
/// Enforces both <c>exp</c> and <c>MaxAssertionAge</c> independently — an unexpired but
/// too-old token is rejected as <see cref="AssertionFailureReason.TokenTooOld"/>.  Returns
/// a structured <see cref="ValidatedAssertion"/> on every code path; does not throw.
/// </para>
/// <para>
/// This validator is one shape among potentially several (see
/// <see cref="IIdentityAssertionValidator"/> for the chain pattern).  Its dispatch check
/// inspects only the assertion's structural shape (three Base64Url segments, <c>alg=HS256</c>);
/// payload claims are never trusted until <see cref="ValidateAsync"/> has succeeded.
/// </para>
/// </summary>
public sealed class OwuiSessionJwtValidator : IIdentityAssertionValidator
{
    public const string Kind = "OwuiSessionJwt";

    // RFC 5321 §4.5.3.1.3 caps an email address at 254 characters.  Tokens carrying
    // pathological email-claim values are rejected as weak identity rather than being
    // truncated silently.
    private const int MaxEmailLength = 254;

    private readonly IdentityBrokerOptions _brokerOptions;
    private readonly OwuiSessionJwtOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OwuiSessionJwtValidator> _logger;

    public OwuiSessionJwtValidator(
        IOptions<GatewayOptions> gatewayOptions,
        TimeProvider timeProvider,
        ILogger<OwuiSessionJwtValidator> logger)
    {
        _brokerOptions = gatewayOptions.Value.IdentityBroker;
        _options = _brokerOptions.OwuiSessionJwt;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public bool CanHandle(IdentityAssertionInput input)
    {
        var rawAssertion = input.RawAssertion;
        if (!_options.Enabled || string.IsNullOrWhiteSpace(rawAssertion))
        {
            return false;
        }

        // Compact JWS has exactly three dot-separated segments: header.payload.signature.
        // Anything else is not an OWUI session JWT.
        var segments = rawAssertion.Split('.');
        if (segments.Length != 3)
        {
            return false;
        }

        // Inspect the (untrusted) header to confirm HS256.  Payload claims are not
        // examined here — that work belongs in ValidateAsync where the signature has been
        // proven first.
        try
        {
            var headerJson = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(segments[0]));
            return headerJson.Contains("\"HS256\"", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // Malformed Base64Url is not for this validator.
            return false;
        }
    }

    public async Task<ValidatedAssertion> ValidateAsync(IdentityAssertionInput input, CancellationToken cancellationToken)
    {
        var rawAssertion = input.RawAssertion ?? string.Empty;
        // Configuration sanity — without a signing key we cannot validate anything.  Fail
        // closed.  Readiness gates startup so this branch should only be hit in tests or
        // a misconfigured broker.
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.SigningKey))
        {
            return ValidatedAssertion.Failure(Kind, AssertionFailureReason.TokenInvalid, "validator-not-configured");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var parameters = BuildValidationParameters();
        var handler = new JsonWebTokenHandler();

        TokenValidationResult result;
        try
        {
            result = await handler.ValidateTokenAsync(rawAssertion, parameters);
        }
        catch (Exception ex)
        {
            // JsonWebTokenHandler is documented to surface failures through the result, but
            // defensively guard against unexpected exceptions so the middleware can fail
            // closed with a stable reason rather than a 500.
            _logger.LogDebug(ex, "OwuiSessionJwt validation threw {ExceptionType}.", ex.GetType().Name);
            return ValidatedAssertion.Failure(Kind, AssertionFailureReason.TokenInvalid, ex.GetType().Name);
        }

        if (!result.IsValid)
        {
            // Microsoft.IdentityModel surfaces lifetime failures through several closely
            // related exception types; collapse them all to TokenExpired so the middleware
            // emits a stable error code regardless of library version.
            var reason = IsExpiredException(result.Exception)
                ? AssertionFailureReason.TokenExpired
                : AssertionFailureReason.TokenInvalid;
            return ValidatedAssertion.Failure(Kind, reason, result.Exception?.GetType().Name);
        }

        if (result.SecurityToken is not JsonWebToken jwt)
        {
            // ValidateTokenAsync should always produce a JsonWebToken on success; treat
            // anything else as invalid rather than reaching for a cast that could throw.
            return ValidatedAssertion.Failure(Kind, AssertionFailureReason.TokenInvalid, "non-jwt-token");
        }

        return ExtractAssertion(jwt);
    }

    private static bool IsExpiredException(Exception? ex)
    {
        if (ex is null) return false;
        // Microsoft.IdentityModel v8+ wraps the expired-token case as
        // SecurityTokenInvalidLifetimeException rather than the older typed
        // SecurityTokenExpiredException.  Treat any lifetime-shaped failure as expired —
        // OWUI session JWTs never use nbf in practice, so collapsing both cases to a
        // TokenExpired result gives the middleware a stable error code without having to
        // pry into the message string.  If a future shape needs to distinguish nbf, we
        // expand here without changing the public failure-reason surface.
        if (ex is SecurityTokenExpiredException) return true;
        if (ex is SecurityTokenInvalidLifetimeException) return true;
        return ex.GetType().Name.Contains("Expired", StringComparison.OrdinalIgnoreCase) ||
               ex.GetType().Name.Contains("Lifetime", StringComparison.OrdinalIgnoreCase);
    }

    private TokenValidationParameters BuildValidationParameters()
    {
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey!));

        return new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(_brokerOptions.ClockSkewSeconds),

            ValidateIssuer = !string.IsNullOrWhiteSpace(_options.Issuer),
            ValidIssuer = _options.Issuer,

            ValidateAudience = !string.IsNullOrWhiteSpace(_options.Audience),
            ValidAudience = _options.Audience,

            // OWUI's session JWTs are HS256.  Allowing only this algorithm rejects an
            // attacker swapping in a different alg header against the same key.
            ValidAlgorithms = ["HS256"]

            // Lifetime validation uses the framework default (DateTime.UtcNow) so an
            // expired exp surfaces as SecurityTokenExpiredException — that gives the caller
            // a stable AssertionFailureReason.TokenExpired.  Max-assertion-age is enforced
            // separately in ExtractAssertion using the injected TimeProvider so age tests
            // remain deterministic.
        };
    }

    private ValidatedAssertion ExtractAssertion(JsonWebToken jwt)
    {
        // Both iat and exp must be present so the MaxAssertionAge bound is enforceable.
        // Without iat we cannot compute the assertion's age; without exp the standard
        // lifetime check could not have succeeded.
        if (!jwt.TryGetPayloadValue<long>("iat", out var iatEpoch) ||
            !jwt.TryGetPayloadValue<long>("exp", out var expEpoch))
        {
            return ValidatedAssertion.Failure(Kind, AssertionFailureReason.TokenWeakIdentity, "iat-or-exp-missing");
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatEpoch);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expEpoch);
        var now = _timeProvider.GetUtcNow();
        var maxAgeSeconds = _options.MaxAssertionAgeSeconds ?? _brokerOptions.MaxAssertionAgeSeconds;
        var maxAge = TimeSpan.FromSeconds(maxAgeSeconds);
        var skew = TimeSpan.FromSeconds(_brokerOptions.ClockSkewSeconds);

        // Independent of exp.  A long-lived exp does not extend the max-age bound.
        if (now - issuedAt > maxAge + skew)
        {
            return ValidatedAssertion.Failure(Kind, AssertionFailureReason.TokenTooOld, $"age={(int)(now - issuedAt).TotalSeconds}s,max={maxAgeSeconds}s");
        }

        var email = NormalizeIdentityClaim(jwt, "email");
        var upn = NormalizeIdentityClaim(jwt, "upn");

        if (email is null && upn is null)
        {
            return ValidatedAssertion.Failure(Kind, AssertionFailureReason.TokenWeakIdentity, "no-email-or-upn");
        }

        return ValidatedAssertion.Success(Kind, email, upn, issuedAt, expiresAt);
    }

    private static string? NormalizeIdentityClaim(JsonWebToken jwt, string claimType)
    {
        if (!jwt.TryGetPayloadValue<string>(claimType, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Trim surrounding whitespace and quotes; lowercase for case-insensitive matching
        // against Graph lookups.
        var normalized = value.Trim().Trim('"').Trim().ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length > MaxEmailLength)
        {
            return null;
        }

        return normalized;
    }
}

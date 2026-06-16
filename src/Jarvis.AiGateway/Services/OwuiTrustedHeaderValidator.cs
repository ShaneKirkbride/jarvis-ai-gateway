using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Validator for the trusted-header deployment path.  Open WebUI v0.8.6+ does not natively
/// forward its session JWT to OpenAI-compatible providers — when
/// <c>ENABLE_FORWARD_USER_INFO_HEADERS=true</c> is set on the OWUI side, it forwards a
/// fixed set of plain HTTP headers (<c>X-OpenWebUI-User-Email</c>, etc.) instead.
/// <para>
/// This validator treats those headers as the identity assertion.  Cryptographic origin
/// trust is provided by ALB-terminated mTLS (the gateway never sees a request whose client
/// cert was not validated by the ALB).  The gateway therefore depends on the
/// <c>OwuiTrustedHeader:RequireMtlsSubjectPinning</c> readiness gate to ensure the mTLS
/// subject is also pinned — without that, a leaked service API key would be enough to
/// assert any identity, which is not acceptable for an ITAR-aware gateway.
/// </para>
/// <para>
/// The validator never accepts group membership from the inbound headers.  Groups are
/// always resolved through Microsoft Graph by the broker.
/// </para>
/// </summary>
public sealed class OwuiTrustedHeaderValidator : IIdentityAssertionValidator
{
    public const string Kind = "OwuiTrustedHeader";

    private readonly OwuiTrustedHeaderOptions _options;

    public OwuiTrustedHeaderValidator(IOptions<GatewayOptions> gatewayOptions)
    {
        _options = gatewayOptions.Value.IdentityBroker.OwuiTrustedHeader;
    }

    public bool CanHandle(IdentityAssertionInput input)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        // We dispatch on the presence of the configured email header — that's the
        // minimum identity claim we need.  If it's absent, this validator declines so the
        // chain can fall through to ValidatorNotFound.
        return TryGetHeader(input, _options.EmailHeader, out _);
    }

    public Task<ValidatedAssertion> ValidateAsync(IdentityAssertionInput input, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(ValidatedAssertion.Failure(Kind, AssertionFailureReason.TokenInvalid, "validator-not-configured"));
        }

        if (!TryGetHeader(input, _options.EmailHeader, out var email))
        {
            return Task.FromResult(ValidatedAssertion.Failure(Kind, AssertionFailureReason.TokenWeakIdentity, "email-header-missing"));
        }

        var normalizedEmail = NormalizeIdentity(email);
        if (normalizedEmail is null)
        {
            return Task.FromResult(ValidatedAssertion.Failure(Kind, AssertionFailureReason.TokenWeakIdentity, "email-malformed"));
        }

        // OWUI does not currently forward a UPN claim; if a future version starts
        // emitting one (e.g. when fronted by Entra), we'll pick it up here automatically.
        TryGetHeader(input, "X-OpenWebUI-User-Upn", out var upn);
        var normalizedUpn = NormalizeIdentity(upn);

        // The trusted-header path has no lifetime — the assertion is bound to the
        // connection lifetime, which is itself bound by mTLS.  We surface "now" as both
        // iat and exp so downstream audit can record consistent timestamps without
        // having to special-case this validator.
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(ValidatedAssertion.Success(Kind, normalizedEmail, normalizedUpn, now, now));
    }

    private static bool TryGetHeader(IdentityAssertionInput input, string headerName, out string? value)
    {
        if (input.TrustedHeaders.TryGetValue(headerName, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = null;
        return false;
    }

    private static string? NormalizeIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().Trim('"').Trim().ToLowerInvariant();
        return trimmed.Length is > 0 and <= 254 ? trimmed : null;
    }
}

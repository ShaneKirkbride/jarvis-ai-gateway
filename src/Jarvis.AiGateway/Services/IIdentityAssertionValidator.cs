namespace Jarvis.AiGateway.Services;

/// <summary>
/// Validator for a signed OWUI-originated identity assertion carried on the gateway's
/// inbound identity header.
/// <para>
/// Multiple implementations may be registered in DI; the broker iterates them and uses the
/// first whose <see cref="CanHandle"/> returns true.  This mirrors the
/// <see cref="IInvokeModelPayloadAdapter"/> pattern already used by the Bedrock invocation
/// strategies.  Adding a new assertion shape — for example a future Jarvis-minted
/// short-lived assertion — is a drop-in DI registration with no middleware change.
/// </para>
/// </summary>
public interface IIdentityAssertionValidator
{
    /// <summary>
    /// Fast structural check used by the broker to dispatch to the right validator without
    /// committing to a full cryptographic validation.  Implementations MUST treat the
    /// inbound content as untrusted here and rely only on structural shape (token
    /// segments, alg header, presence of a known trusted-header, etc.).  Returning true
    /// does not assert the input is valid — only that this validator is willing to attempt
    /// validation.
    /// </summary>
    bool CanHandle(IdentityAssertionInput input);

    /// <summary>
    /// Validate the assertion input and extract the canonical-identity claims that the
    /// broker needs to drive Microsoft Graph lookup and the resulting principal.
    /// Implementations MUST fail closed on any error: signature failures, expired tokens,
    /// tokens older than the configured max age, weak identity (no email/upn), or missing
    /// trusted headers must return a non-<see cref="ValidatedAssertion.IsValid"/> result
    /// rather than throw.
    /// </summary>
    Task<ValidatedAssertion> ValidateAsync(IdentityAssertionInput input, CancellationToken cancellationToken);
}

/// <summary>
/// Everything a validator needs to make its decision.  Carries the optional token string
/// (for JWT/bearer-style validators like <c>OwuiSessionJwtValidator</c>) AND a snapshot of
/// the request's trusted-source HTTP headers (for header-based validators like
/// <c>OwuiTrustedHeaderValidator</c>).  Header keys are case-insensitive; values may be
/// null when a configured header is absent on the request.
/// <para>
/// The middleware constructs this input from <see cref="HttpContext"/>; the broker passes
/// it to each registered validator.  Validators do not access <see cref="HttpContext"/>
/// directly so they remain trivially unit-testable.
/// </para>
/// </summary>
public sealed record IdentityAssertionInput(
    string? RawAssertion,
    IReadOnlyDictionary<string, string?> TrustedHeaders)
{
    public static readonly IdentityAssertionInput Empty =
        new(null, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Outcome of an attempt to validate an inbound identity assertion.  Carries the
/// canonical-identity claims on success and a structured failure reason on failure.
/// Never carries the raw assertion, signing key, or any secret material.
/// </summary>
public sealed record ValidatedAssertion
{
    public bool IsValid { get; init; }

    /// <summary>
    /// Short, non-secret descriptor of which validator produced this result.  Surfaced into
    /// audit events so reviewers can see which assertion shape was in play at decision time.
    /// </summary>
    public string AssertionKind { get; init; } = string.Empty;

    public string? Email { get; init; }
    public string? Upn { get; init; }
    public DateTimeOffset? IssuedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    public AssertionFailureReason FailureReason { get; init; } = AssertionFailureReason.None;

    /// <summary>
    /// Non-secret diagnostic hint for audit/logging.  Typically the exception type name or
    /// a short reason string.  Never includes token contents or keys.
    /// </summary>
    public string? DiagnosticHint { get; init; }

    public static ValidatedAssertion Success(string kind, string? email, string? upn, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
        => new()
        {
            IsValid = true,
            AssertionKind = kind,
            Email = email,
            Upn = upn,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt
        };

    public static ValidatedAssertion Failure(string kind, AssertionFailureReason reason, string? hint = null)
        => new()
        {
            IsValid = false,
            AssertionKind = kind,
            FailureReason = reason,
            DiagnosticHint = hint
        };
}

/// <summary>
/// Stable, non-secret enumeration of identity-resolution failure modes shared between the
/// validator chain, the broker, and the middleware.  Stable values let monitoring and SIEM
/// rules key on the reason without parsing free-text messages.
/// </summary>
public enum AssertionFailureReason
{
    None = 0,

    /// <summary>The identity header was absent or empty.  Set by middleware, not by validators.</summary>
    TokenMissing,

    /// <summary>Signature failed, payload malformed, issuer/audience mismatch, or other structural rejection.</summary>
    TokenInvalid,

    /// <summary><c>exp</c> is in the past beyond the configured clock skew.</summary>
    TokenExpired,

    /// <summary><c>iat</c> is older than the configured max assertion age (independent of <c>exp</c>).</summary>
    TokenTooOld,

    /// <summary>The assertion validated but did not carry the minimum canonical-identity claims (no email or upn).</summary>
    TokenWeakIdentity,

    /// <summary>No registered validator's <see cref="IIdentityAssertionValidator.CanHandle"/> returned true.</summary>
    ValidatorNotFound,

    /// <summary>Microsoft Graph could not resolve group membership due to a transient failure.</summary>
    GraphLookupFailed,

    /// <summary>Microsoft Graph reported the canonical subject does not exist in the directory.</summary>
    GraphUserNotFound
}

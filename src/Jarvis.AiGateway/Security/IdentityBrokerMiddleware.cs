using System.Security.Claims;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Security;

/// <summary>
/// Replaces the legacy JwtBearer authentication for protected routes when the identity
/// broker is enabled.  Validates the inbound assertion, optionally pins the ALB-forwarded
/// mTLS subject metadata, resolves Graph groups via <see cref="IIdentityBroker"/>, and
/// populates <see cref="HttpContext.User"/> with a <see cref="ClaimsPrincipal"/> whose
/// group claims are Entra object IDs (display names are attached separately for
/// diagnostics and are never read by the policy engine).
/// <para>
/// Pre-auth rate limiting happens BEFORE this middleware in the pipeline, so a flood
/// without an identity header is rejected at the limiter without ever invoking the
/// validator chain or Graph.
/// </para>
/// </summary>
public sealed class IdentityBrokerMiddleware
{
    public const string AuthenticationScheme = "JarvisIdentityBroker";
    public const string GroupIdClaim = "jarvis:group_id";
    public const string GroupNameClaim = "jarvis:group_name";
    public const string EntraObjectIdClaim = "jarvis:oid";
    public const string AssertionKindClaim = "jarvis:assertion_kind";

    // ALB mutual-TLS verify mode forwards a small set of metadata headers about the
    // validated client cert.  The gateway does NOT parse a full cert PEM — that lives in
    // a passthrough-mode topology which we do not currently use.  See plan §7.
    public const string AlbMtlsSubjectHeader = "X-Amzn-Mtls-Clientcert-Subject";
    public const string AlbMtlsSerialHeader = "X-Amzn-Mtls-Clientcert-Serial-Number";

    private readonly RequestDelegate _next;
    private readonly IIdentityBroker _broker;
    private readonly IAuditLogger _auditLogger;
    private readonly ISubjectHasher _subjectHasher;
    private readonly IdentityBrokerOptions _options;

    public IdentityBrokerMiddleware(
        RequestDelegate next,
        IIdentityBroker broker,
        IAuditLogger auditLogger,
        ISubjectHasher subjectHasher,
        IOptions<GatewayOptions> gatewayOptions)
    {
        _next = next;
        _broker = broker;
        _auditLogger = auditLogger;
        _subjectHasher = subjectHasher;
        _options = gatewayOptions.Value.IdentityBroker;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // The broker can be disabled at runtime even when its DI graph and middleware are
        // registered (e.g. a config rollback or a WebApplicationFactory test override that
        // arrives after Program.cs's up-front capture).  In that case the legacy auth path
        // owns HttpContext.User; pass through without overwriting it.
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        if (IsHealthOrReadinessProbe(context))
        {
            await _next(context);
            return;
        }

        if (IsModelsListing(context) && !_options.ModelsEndpointRequiresUser)
        {
            // OWUI deployments that call /v1/models in a service context (no user session)
            // continue with an anonymous principal; the policy engine then filters out
            // ITAR-approved aliases that require any group membership.  Treat as a
            // deliberate Option-B fall-through — emitted at debug to keep volume low.
            await _next(context);
            return;
        }

        if (!ValidateMtlsSubject(context, out var observedSubject))
        {
            WriteMtlsAudit(context, observedSubject);
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized,
                "IDENTITY_TOKEN_INVALID", "Client certificate subject not accepted.");
            return;
        }

        if (!TryReadAssertion(context, out var rawAssertion, out var headerFailure))
        {
            // If the broker has a trusted-header path active, the absence of the token
            // header is not necessarily fatal — the broker will dispatch to the trusted-
            // header validator if its required headers are present.  We only fail early
            // when the token header is malformed (multiple values), never when it's
            // simply missing.
            if (headerFailure != AssertionFailureReason.TokenMissing || !_options.OwuiTrustedHeader.Enabled)
            {
                await HandleEarlyHeaderFailureAsync(context, headerFailure);
                return;
            }
        }

        var input = new IdentityAssertionInput(rawAssertion, CaptureTrustedHeaders(context));
        var result = await _broker.ResolveAsync(input, context.RequestAborted);

        if (!result.IsValid)
        {
            await HandleFailureAsync(context, result);
            return;
        }

        context.User = BuildPrincipal(result);
        WriteResolvedAudit(context, result);

        await _next(context);
    }

    // ── Skip predicates ──────────────────────────────────────────────────────────────

    private static bool IsHealthOrReadinessProbe(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/healthz") ||
        context.Request.Path.StartsWithSegments("/readyz");

    private static bool IsModelsListing(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/v1/models");

    // ── mTLS subject pinning ─────────────────────────────────────────────────────────

    private bool ValidateMtlsSubject(HttpContext context, out string? observedSubject)
    {
        observedSubject = null;

        if (!_options.Mtls.RequireSubjectCheck)
        {
            return true;
        }

        var hasSubjectAllowlist = _options.Mtls.AcceptedClientCertSubjects.Count > 0;
        var hasSerialAllowlist = _options.Mtls.AcceptedClientCertSerials.Count > 0;

        if (hasSubjectAllowlist)
        {
            if (!context.Request.Headers.TryGetValue(AlbMtlsSubjectHeader, out var subject) || subject.Count != 1)
            {
                return false;
            }
            observedSubject = subject.ToString();
            if (!_options.Mtls.AcceptedClientCertSubjects.Contains(observedSubject, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (hasSerialAllowlist)
        {
            if (!context.Request.Headers.TryGetValue(AlbMtlsSerialHeader, out var serial) || serial.Count != 1)
            {
                return false;
            }
            if (!_options.Mtls.AcceptedClientCertSerials.Contains(serial.ToString(), StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // ── Header reading ───────────────────────────────────────────────────────────────

    private IReadOnlyDictionary<string, string?> CaptureTrustedHeaders(HttpContext context)
    {
        // Snapshot the configured trusted-header set into a case-insensitive bag so the
        // validator can read headers without holding HttpContext.  We do NOT pass the full
        // header collection to keep the surface area small and to make the trust contract
        // explicit in code review.
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!_options.OwuiTrustedHeader.Enabled)
        {
            return headers;
        }

        AddIfPresent(context, headers, _options.OwuiTrustedHeader.EmailHeader);
        AddIfPresent(context, headers, _options.OwuiTrustedHeader.IdHeader);
        AddIfPresent(context, headers, _options.OwuiTrustedHeader.NameHeader);
        AddIfPresent(context, headers, _options.OwuiTrustedHeader.RoleHeader);
        return headers;
    }

    private static void AddIfPresent(HttpContext context, IDictionary<string, string?> bag, string headerName)
    {
        if (string.IsNullOrWhiteSpace(headerName)) return;
        if (context.Request.Headers.TryGetValue(headerName, out var values) && values.Count == 1)
        {
            bag[headerName] = values[0];
        }
    }

    private bool TryReadAssertion(HttpContext context, out string? rawAssertion, out AssertionFailureReason failure)
    {
        rawAssertion = null;
        failure = AssertionFailureReason.None;

        if (!context.Request.Headers.TryGetValue(_options.TokenHeader, out var headerValues))
        {
            failure = AssertionFailureReason.TokenMissing;
            return false;
        }

        // The plan forbids ambiguity — multiple values on the identity header is treated
        // as TokenInvalid, never silently coalesced.
        if (headerValues.Count != 1)
        {
            failure = AssertionFailureReason.TokenInvalid;
            return false;
        }

        var raw = headerValues[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            failure = AssertionFailureReason.TokenMissing;
            return false;
        }

        // Strip an optional "Bearer " prefix for compatibility with clients that send the
        // assertion as if it were a bearer token.  This is purely cosmetic — the validator
        // chain is what proves authenticity.
        const string bearerPrefix = "Bearer ";
        if (raw.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[bearerPrefix.Length..].TrimStart();
        }

        rawAssertion = raw;
        return true;
    }

    // ── Failure responses ────────────────────────────────────────────────────────────

    private async Task HandleEarlyHeaderFailureAsync(HttpContext context, AssertionFailureReason failure)
    {
        var earlyResult = new IdentityAssertionResult { IsValid = false, FailureReason = failure };
        await HandleFailureAsync(context, earlyResult);
    }

    private async Task HandleFailureAsync(HttpContext context, IdentityAssertionResult result)
    {
        var (status, code, message) = MapFailure(result.FailureReason);
        WriteFailureAudit(context, result);
        await WriteErrorAsync(context, status, code, message);
    }

    private static (int Status, string Code, string Message) MapFailure(AssertionFailureReason reason) => reason switch
    {
        AssertionFailureReason.TokenMissing =>
            (StatusCodes.Status401Unauthorized, "IDENTITY_TOKEN_MISSING", "Identity header is required."),
        AssertionFailureReason.TokenInvalid =>
            (StatusCodes.Status401Unauthorized, "IDENTITY_TOKEN_INVALID", "Identity token is invalid."),
        AssertionFailureReason.TokenExpired =>
            (StatusCodes.Status401Unauthorized, "IDENTITY_TOKEN_EXPIRED", "Identity token is expired."),
        AssertionFailureReason.TokenTooOld =>
            (StatusCodes.Status401Unauthorized, "IDENTITY_TOKEN_TOO_OLD", "Identity token exceeds maximum allowed age."),
        AssertionFailureReason.TokenWeakIdentity =>
            (StatusCodes.Status401Unauthorized, "IDENTITY_TOKEN_WEAK", "Identity token does not carry a usable subject."),
        AssertionFailureReason.ValidatorNotFound =>
            (StatusCodes.Status401Unauthorized, "IDENTITY_TOKEN_INVALID", "Identity token shape is not recognized."),
        AssertionFailureReason.GraphLookupFailed =>
            (StatusCodes.Status503ServiceUnavailable, "IDENTITY_RESOLUTION_UNAVAILABLE", "Identity resolution is temporarily unavailable."),
        AssertionFailureReason.GraphUserNotFound =>
            (StatusCodes.Status403Forbidden, "IDENTITY_USER_NOT_FOUND", "Identity subject is not present in the directory."),
        _ =>
            (StatusCodes.Status401Unauthorized, "IDENTITY_TOKEN_INVALID", "Identity token is invalid.")
    };

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string errorCode, string message)
    {
        // Client-facing messages stay generic per .claude/rules/api-conventions.md — the
        // structured reason lives in the audit event only.  Using the OpenAI error envelope
        // keeps the wire shape identical to the rest of the gateway's error responses.
        context.Response.StatusCode = statusCode;
        var envelope = OpenAiErrorResponse.Create(message, type: "invalid_request_error", code: errorCode);
        await context.Response.WriteAsJsonAsync(envelope);
    }

    // ── Principal construction ───────────────────────────────────────────────────────

    private static ClaimsPrincipal BuildPrincipal(IdentityAssertionResult result)
    {
        var claims = new List<Claim>
        {
            new("sub", result.CanonicalSubject!),
            new(ClaimTypes.NameIdentifier, result.CanonicalSubject!)
        };

        if (!string.IsNullOrEmpty(result.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, result.Email));
            claims.Add(new Claim("email", result.Email));
        }
        if (!string.IsNullOrEmpty(result.Upn))
        {
            claims.Add(new Claim("upn", result.Upn));
        }
        if (!string.IsNullOrEmpty(result.EntraObjectId))
        {
            claims.Add(new Claim(EntraObjectIdClaim, result.EntraObjectId));
        }
        if (!string.IsNullOrEmpty(result.AssertionKind))
        {
            claims.Add(new Claim(AssertionKindClaim, result.AssertionKind));
        }

        foreach (var group in result.Groups)
        {
            // Entra object IDs are the authoritative policy input — added as both a custom
            // jarvis claim AND ClaimTypes.Role so existing role-based ASP.NET authorization
            // helpers continue to work transparently.
            claims.Add(new Claim(GroupIdClaim, group.Id));
            claims.Add(new Claim(ClaimTypes.Role, group.Id));

            if (!string.IsNullOrWhiteSpace(group.DisplayName))
            {
                claims.Add(new Claim(GroupNameClaim, group.DisplayName));
            }
        }

        var identity = new ClaimsIdentity(claims, AuthenticationScheme, "sub", ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    // ── Audit emission ───────────────────────────────────────────────────────────────

    private void WriteResolvedAudit(HttpContext context, IdentityAssertionResult result)
    {
        var groupIds = result.Groups.Select(g => g.Id).ToArray();
        _auditLogger.WriteIdentity(new IdentityAuditEvent
        {
            EventName = "identity.resolved",
            Level = "info",
            CorrelationId = context.TraceIdentifier,
            RequestPath = context.Request.Path,
            RequestMethod = context.Request.Method,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            HashedSubject = _subjectHasher.Hash(result.CanonicalSubject),
            EmailDomain = ExtractEmailDomain(result.Email),
            HashedOid = _subjectHasher.Hash(result.EntraObjectId),
            GroupCount = groupIds.Length,
            GroupIds = groupIds,
            AssertionKind = result.AssertionKind,
            IdentitySource = result.IdentitySource.ToString()
        });
    }

    private void WriteFailureAudit(HttpContext context, IdentityAssertionResult result)
    {
        var (eventName, level) = result.FailureReason switch
        {
            AssertionFailureReason.TokenMissing => ("identity.token.missing", "warn"),
            AssertionFailureReason.TokenInvalid => ("identity.token.invalid", "warn"),
            AssertionFailureReason.TokenExpired => ("identity.token.expired", "warn"),
            AssertionFailureReason.TokenTooOld => ("identity.token.too_old", "warn"),
            AssertionFailureReason.TokenWeakIdentity => ("identity.token.weak", "warn"),
            AssertionFailureReason.ValidatorNotFound => ("identity.validator.not_found", "warn"),
            AssertionFailureReason.GraphLookupFailed => ("identity.lookup.failed", "error"),
            AssertionFailureReason.GraphUserNotFound => ("identity.user_not_found", "warn"),
            _ => ("identity.token.invalid", "warn")
        };

        _auditLogger.WriteIdentity(new IdentityAuditEvent
        {
            EventName = eventName,
            Level = level,
            CorrelationId = context.TraceIdentifier,
            RequestPath = context.Request.Path,
            RequestMethod = context.Request.Method,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            HashedSubject = _subjectHasher.Hash(result.CanonicalSubject),
            EmailDomain = ExtractEmailDomain(result.Email),
            AssertionKind = result.AssertionKind,
            FailureReason = result.FailureReason.ToString(),
            DiagnosticHint = result.DiagnosticHint
        });
    }

    private void WriteMtlsAudit(HttpContext context, string? observedSubject)
    {
        _auditLogger.WriteIdentity(new IdentityAuditEvent
        {
            EventName = "mtls.subject_unexpected",
            Level = "warn",
            CorrelationId = context.TraceIdentifier,
            RequestPath = context.Request.Path,
            RequestMethod = context.Request.Method,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            MtlsObservedSubject = observedSubject,
            MtlsAcceptedSubjects = _options.Mtls.AcceptedClientCertSubjects
        });
    }

    private static string? ExtractEmailDomain(string? email)
    {
        if (string.IsNullOrEmpty(email)) return null;
        var atIndex = email.LastIndexOf('@');
        return atIndex >= 0 && atIndex < email.Length - 1 ? email[(atIndex + 1)..] : null;
    }
}

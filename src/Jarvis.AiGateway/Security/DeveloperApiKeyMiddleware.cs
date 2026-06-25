using System.Security.Claims;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Security;

/// <summary>
/// Marks a request as authenticated by a developer API key.  The flag is set server-side only
/// after a validated store lookup, so the service-key and identity-broker middlewares can treat a
/// developer-authenticated request as already satisfying gateway entry without re-prompting for
/// their own credential.  It cannot be set by a client.
/// </summary>
public static class DeveloperApiKeyContext
{
    public const string AuthenticatedItem = "jarvis.developer_authenticated";

    public static bool IsAuthenticated(HttpContext context) =>
        context.Items.TryGetValue(AuthenticatedItem, out var value) && value is true;
}

/// <summary>
/// Additive authentication path for developer/IDE clients presenting
/// <c>Authorization: Bearer jrvs_…</c>.  It only acts on bearers carrying the configured developer
/// prefix — legacy JWT bearers and service-key requests pass straight through.  A valid key
/// establishes a user <see cref="ClaimsPrincipal"/> (authorization-equivalent to the JWT/broker
/// path); any invalid/expired/revoked/malformed key fails closed with an OpenAI-compatible error
/// that does not reveal which condition failed.
/// </summary>
public sealed class DeveloperApiKeyMiddleware(
    RequestDelegate next,
    IDeveloperApiKeyAuthenticator authenticator,
    IOptions<GatewayOptions> options,
    IAuditLogger auditLogger,
    ISubjectHasher subjectHasher)
{
    private readonly DeveloperAuthOptions _options = options.Value.DeveloperAuth;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled || !TryReadDeveloperBearer(context, out var presentedKey))
        {
            await next(context);
            return;
        }

        var result = await authenticator.AuthenticateAsync(presentedKey, context.RequestAborted);
        if (result.IsAuthenticated)
        {
            context.User = result.Principal!;
            context.Items[DeveloperApiKeyContext.AuthenticatedItem] = true;
            WriteAcceptedAudit(context, result);
            await next(context);
            return;
        }

        WriteRejectedAudit(context, result);
        var (status, code, message) = Map(result.Outcome);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(OpenAiErrorResponse.Create(message, "invalid_request_error", code));
    }

    private bool TryReadDeveloperBearer(HttpContext context, out string presentedKey)
    {
        presentedKey = string.Empty;
        if (!context.Request.Headers.TryGetValue("Authorization", out var values) || values.Count != 1)
        {
            return false;
        }

        var raw = values[0];
        const string bearer = "Bearer ";
        if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = raw[bearer.Length..].Trim();
        // Only consume tokens carrying the developer prefix; legacy JWT bearers are left untouched.
        if (!token.StartsWith(_options.KeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        presentedKey = token;
        return true;
    }

    private static (int Status, string Code, string Message) Map(DeveloperApiKeyOutcome outcome) => outcome switch
    {
        DeveloperApiKeyOutcome.ResolutionUnavailable =>
            (StatusCodes.Status503ServiceUnavailable, "IDENTITY_RESOLUTION_UNAVAILABLE", "Identity resolution is temporarily unavailable."),
        DeveloperApiKeyOutcome.OwnerNotFound =>
            (StatusCodes.Status403Forbidden, "IDENTITY_USER_NOT_FOUND", "API key owner is not present in the directory."),
        // Malformed / InvalidKey / Expired / Revoked all return one generic 401 so the response
        // never reveals whether a key exists or why it failed.
        _ => (StatusCodes.Status401Unauthorized, "INVALID_API_KEY", "Invalid API key.")
    };

    private void WriteAcceptedAudit(HttpContext context, DeveloperApiKeyAuthResult result)
    {
        auditLogger.WriteIdentity(new IdentityAuditEvent
        {
            EventName = "developer_api_key.accepted",
            Level = "info",
            CorrelationId = context.TraceIdentifier,
            RequestPath = context.Request.Path,
            RequestMethod = context.Request.Method,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            HashedSubject = subjectHasher.Hash(result.Principal?.FindFirstValue("sub")),
            IdentitySource = DeveloperApiKeyClaims.AuthTypeValue,
            DiagnosticHint = $"key_id={result.KeyId};fp={result.Fingerprint}"
        });
    }

    private void WriteRejectedAudit(HttpContext context, DeveloperApiKeyAuthResult result)
    {
        auditLogger.WriteIdentity(new IdentityAuditEvent
        {
            EventName = "developer_api_key.rejected",
            Level = "warn",
            CorrelationId = context.TraceIdentifier,
            RequestPath = context.Request.Path,
            RequestMethod = context.Request.Method,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            FailureReason = result.Outcome.ToString(),
            // Never the raw key: only the non-reversible fingerprint and (if matched) the key id.
            DiagnosticHint = result.KeyId is null ? $"fp={result.Fingerprint}" : $"key_id={result.KeyId};fp={result.Fingerprint}"
        });
    }
}

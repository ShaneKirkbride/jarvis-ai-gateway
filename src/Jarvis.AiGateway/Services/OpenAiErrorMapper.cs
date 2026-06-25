using Amazon.BedrockRuntime.Model;
using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

public interface IOpenAiErrorMapper
{
    OpenAiErrorMapping MapValidation(OpenAiChatValidationResult validationResult);
    OpenAiErrorMapping MapPolicyDenied(PolicyDecision decision);
    OpenAiErrorMapping MapException(Exception exception);
}

public sealed record OpenAiErrorMapping(int StatusCode, OpenAiErrorResponse Response, string ErrorType, string ErrorCategory);

public sealed class ProviderTimeoutException(string message, Exception? innerException = null) : TimeoutException(message, innerException);

/// <summary>
/// Thrown when a Bedrock provider response cannot be parsed.
/// Mapped to HTTP 502 with a generic message so internal details are not leaked.
/// </summary>
public sealed class ProviderResponseParseException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Thrown when Azure OpenAI returns a non-success HTTP status.  Carries only the status code —
/// never the response body — so provider-internal detail is not surfaced to callers.
/// </summary>
public sealed class AzureOpenAiException(System.Net.HttpStatusCode statusCode, Exception? innerException = null)
    : Exception($"Azure OpenAI returned HTTP {(int)statusCode}.", innerException)
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed class OpenAiErrorMapper : IOpenAiErrorMapper
{
    public OpenAiErrorMapping MapValidation(OpenAiChatValidationResult validationResult)
    {
        var first = validationResult.Errors.FirstOrDefault();
        var message = first is null ? "Invalid request." : first.Message;
        var code = first?.Code ?? "invalid_request";
        return new OpenAiErrorMapping(
            StatusCodes.Status400BadRequest,
            OpenAiErrorResponse.Create(message, "invalid_request_error", code),
            code,
            "validation");
    }

    // Use a generic client-facing message for ALL policy denials to avoid leaking internal
    // policy state (ITAR model approval, workspace approval, group requirements) to callers.
    // The specific reason is captured in the audit log via audit.policy_decision and
    // audit.policy_rule_id.  The stable RuleId (e.g. ITAR_MODEL_DENIED) is safe to surface
    // as an error code for monitoring/alerting without revealing the approval logic.
    public OpenAiErrorMapping MapPolicyDenied(PolicyDecision decision) => new(
        StatusCodes.Status403Forbidden,
        OpenAiErrorResponse.Create("Request is not allowed by policy.", "invalid_request_error", decision.RuleId),
        decision.RuleId,
        "policy");

    public OpenAiErrorMapping MapException(Exception exception)
    {
        return exception switch
        {
            ProviderTimeoutException => new OpenAiErrorMapping(
                StatusCodes.Status504GatewayTimeout,
                OpenAiErrorResponse.Create("Provider invocation timed out.", "server_error", "provider_timeout"),
                "provider_timeout",
                "provider"),
            OperationCanceledException => new OpenAiErrorMapping(
                StatusCodes.Status499ClientClosedRequest,
                OpenAiErrorResponse.Create("Request was cancelled.", "server_error", "request_cancelled"),
                "request_cancelled",
                "cancellation"),
            NotSupportedException => new OpenAiErrorMapping(
                StatusCodes.Status501NotImplemented,
                OpenAiErrorResponse.Create("The requested model is not supported by this gateway invocation adapter.", "invalid_request_error", "unsupported_model"),
                "unsupported_model",
                "capability"),
            ProviderResponseParseException => new OpenAiErrorMapping(
                StatusCodes.Status502BadGateway,
                OpenAiErrorResponse.Create("Provider returned an unexpected response format.", "server_error", "provider_parse_error"),
                "provider_parse_error",
                "provider"),
            ThrottlingException => new OpenAiErrorMapping(
                StatusCodes.Status429TooManyRequests,
                OpenAiErrorResponse.Create("Provider throttled the request.", "server_error", "provider_throttled"),
                "provider_throttled",
                "provider"),
            AccessDeniedException => new OpenAiErrorMapping(
                StatusCodes.Status502BadGateway,
                OpenAiErrorResponse.Create("Provider access was denied or misconfigured.", "server_error", "provider_access_denied"),
                "provider_access_denied",
                "provider"),
            Amazon.BedrockRuntime.Model.ValidationException => new OpenAiErrorMapping(
                StatusCodes.Status502BadGateway,
                OpenAiErrorResponse.Create("Provider rejected the invocation request.", "server_error", "provider_validation_error"),
                "provider_validation_error",
                "provider"),
            AzureOpenAiException azure => MapAzure(azure),
            _ => new OpenAiErrorMapping(
                StatusCodes.Status502BadGateway,
                OpenAiErrorResponse.Create("Gateway provider invocation failed.", "server_error", "gateway_error"),
                "gateway_error",
                "unexpected")
        };
    }

    // Maps Azure OpenAI HTTP statuses to stable, curated client-facing codes.  The provider-side
    // status, error code, message, and body are NEVER echoed verbatim — only these curated values.
    private static OpenAiErrorMapping MapAzure(AzureOpenAiException exception) => (int)exception.StatusCode switch
    {
        // Invalid request / unsupported parameter (e.g. a model-incompatible field).
        400 => new OpenAiErrorMapping(
            StatusCodes.Status502BadGateway,
            OpenAiErrorResponse.Create("Provider rejected the invocation request.", "server_error", "provider_validation_error"),
            "provider_validation_error",
            "provider"),
        // Provider authentication / authorization failure (bad or missing key / identity).
        401 or 403 => new OpenAiErrorMapping(
            StatusCodes.Status502BadGateway,
            OpenAiErrorResponse.Create("Provider authentication failed.", "server_error", "provider_auth_error"),
            "provider_auth_error",
            "provider"),
        // Throttling — surfaced as 429 so callers can back off.
        429 => new OpenAiErrorMapping(
            StatusCodes.Status429TooManyRequests,
            OpenAiErrorResponse.Create("Provider throttled the request.", "server_error", "provider_throttled"),
            "provider_throttled",
            "provider"),
        // Transient/server-side provider failures.
        >= 500 => new OpenAiErrorMapping(
            StatusCodes.Status503ServiceUnavailable,
            OpenAiErrorResponse.Create("Provider is temporarily unavailable.", "server_error", "provider_unavailable"),
            "provider_unavailable",
            "provider"),
        // Any other non-success status.
        _ => new OpenAiErrorMapping(
            StatusCodes.Status502BadGateway,
            OpenAiErrorResponse.Create("Gateway provider invocation failed.", "server_error", "provider_error"),
            "provider_error",
            "provider")
    };
}

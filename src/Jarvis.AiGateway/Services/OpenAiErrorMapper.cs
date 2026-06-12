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

    public OpenAiErrorMapping MapPolicyDenied(PolicyDecision decision) => new(
        StatusCodes.Status403Forbidden,
        OpenAiErrorResponse.Create(decision.Reason, "invalid_request_error", decision.RuleId),
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
            _ => new OpenAiErrorMapping(
                StatusCodes.Status502BadGateway,
                OpenAiErrorResponse.Create("Gateway provider invocation failed.", "server_error", "gateway_error"),
                "gateway_error",
                "unexpected")
        };
    }
}

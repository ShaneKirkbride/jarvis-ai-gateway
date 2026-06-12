using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IOpenAiChatRequestValidator
{
    OpenAiChatValidationResult Validate(HttpContext httpContext, OpenAiChatCompletionRequest? request, GatewayModel? model = null);
}

public sealed record OpenAiChatValidationResult(bool IsValid, AiChatRequest? AiRequest, IReadOnlyList<OpenAiValidationError> Errors)
{
    public static OpenAiChatValidationResult Success(AiChatRequest request) => new(true, request, []);
    public static OpenAiChatValidationResult Failure(params OpenAiValidationError[] errors) => new(false, null, errors);
}

public sealed record OpenAiValidationError(string Code, string Message, string Target);

public sealed class OpenAiChatRequestValidator(IOptions<GatewayOptions> options) : IOpenAiChatRequestValidator
{
    private static readonly HashSet<string> SupportedRoles = new(StringComparer.OrdinalIgnoreCase) { "system", "user", "assistant" };
    private readonly GatewayOptions _options = options.Value;

    public OpenAiChatValidationResult Validate(HttpContext httpContext, OpenAiChatCompletionRequest? request, GatewayModel? model = null)
    {
        var errors = new List<OpenAiValidationError>();
        if (request is null)
        {
            return OpenAiChatValidationResult.Failure(new OpenAiValidationError("invalid_request", "Request body is required.", "body"));
        }

        ValidateRelevantHeaders(httpContext, errors);

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            errors.Add(new OpenAiValidationError("model_required", "model is required.", "model"));
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            errors.Add(new OpenAiValidationError("messages_required", "messages is required and must contain at least one message.", "messages"));
        }
        else if (request.Messages.Count > _options.RequestLimits.MaxMessageCount)
        {
            errors.Add(new OpenAiValidationError("messages_too_large", $"messages must contain no more than {_options.RequestLimits.MaxMessageCount} entries.", "messages"));
        }

        if (request.Temperature is < 0 or > 1)
        {
            errors.Add(new OpenAiValidationError("temperature_out_of_range", "temperature must be between 0 and 1.", "temperature"));
        }

        if (request.TopP is < 0 or > 1)
        {
            errors.Add(new OpenAiValidationError("top_p_out_of_range", "top_p must be between 0 and 1.", "top_p"));
        }

        if (request.MaxTokens is <= 0)
        {
            errors.Add(new OpenAiValidationError("max_tokens_invalid", "max_tokens must be positive when supplied.", "max_tokens"));
        }

        if (model is not null && request.MaxTokens.HasValue && request.MaxTokens.Value > model.MaxOutputTokens)
        {
            errors.Add(new OpenAiValidationError("max_tokens_exceeds_model_limit", $"max_tokens must not exceed the configured model maximum of {model.MaxOutputTokens}.", "max_tokens"));
        }

        var stopSequences = ValidateStopSequences(request.Stop, errors);
        ValidateMetadata(request.Metadata, errors);

        var aiMessages = new List<AiMessage>();
        if (request.Messages is not null)
        {
            for (var i = 0; i < request.Messages.Count; i++)
            {
                var message = request.Messages[i];
                if (string.IsNullOrWhiteSpace(message.Role) || !SupportedRoles.Contains(message.Role))
                {
                    errors.Add(new OpenAiValidationError("unsupported_role", $"messages[{i}].role must be one of system, user, or assistant.", $"messages[{i}].role"));
                    continue;
                }

                if (!message.TryGetTextContent(out var text, out var error))
                {
                    errors.Add(new OpenAiValidationError("unsupported_content", $"messages[{i}].content {error}", $"messages[{i}].content"));
                    continue;
                }

                aiMessages.Add(new AiMessage(message.Role, text));
            }
        }

        if (aiMessages.Count > 0 && !aiMessages.Any(m => !m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content)))
        {
            errors.Add(new OpenAiValidationError("non_system_message_required", "At least one non-system message with text content is required.", "messages"));
        }

        if (errors.Count > 0)
        {
            return new OpenAiChatValidationResult(false, null, errors);
        }

        var metadata = request.Metadata?.ToDictionary(kvp => kvp.Key, kvp => MetadataValueToString(kvp.Value), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return OpenAiChatValidationResult.Success(new AiChatRequest(
            request.Model,
            aiMessages,
            new AiGenerationOptions(request.Temperature, request.TopP, request.MaxTokens, stopSequences),
            metadata,
            request.Stream));
    }

    private void ValidateRelevantHeaders(HttpContext httpContext, List<OpenAiValidationError> errors)
    {
        foreach (var header in new[] { "X-Correlation-Id", "X-Request-Id", "X-Jarvis-Workspace-Id", "X-Jarvis-Data-Label", "X-Jarvis-Itar-Mode" })
        {
            if (httpContext.Request.Headers.TryGetValue(header, out var value) && value.ToString().Length > _options.RequestLimits.MaxGatewayHeaderLength)
            {
                errors.Add(new OpenAiValidationError("header_too_large", $"{header} exceeds the maximum allowed length.", header));
            }
        }
    }

    private IReadOnlyList<string> ValidateStopSequences(JsonElement? stop, List<OpenAiValidationError> errors)
    {
        if (stop is null) return [];
        var sequences = new List<string>();
        var element = stop.Value;
        if (element.ValueKind == JsonValueKind.String)
        {
            sequences.Add(element.GetString() ?? string.Empty);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new OpenAiValidationError("stop_invalid", "stop must be a string or an array of strings.", "stop"));
                    return sequences;
                }

                sequences.Add(item.GetString() ?? string.Empty);
            }
        }
        else
        {
            errors.Add(new OpenAiValidationError("stop_invalid", "stop must be a string or an array of strings.", "stop"));
            return sequences;
        }

        if (sequences.Count > _options.RequestLimits.MaxStopSequenceCount)
        {
            errors.Add(new OpenAiValidationError("stop_too_many", $"stop must contain no more than {_options.RequestLimits.MaxStopSequenceCount} sequences.", "stop"));
        }

        if (sequences.Any(s => s.Length > _options.RequestLimits.MaxStopSequenceLength))
        {
            errors.Add(new OpenAiValidationError("stop_too_long", $"stop sequences must not exceed {_options.RequestLimits.MaxStopSequenceLength} characters.", "stop"));
        }

        return sequences.Where(s => !string.IsNullOrWhiteSpace(s)).Take(_options.RequestLimits.MaxStopSequenceCount).ToArray();
    }

    private void ValidateMetadata(Dictionary<string, JsonElement>? metadata, List<OpenAiValidationError> errors)
    {
        if (metadata is null) return;
        if (metadata.Count > _options.RequestLimits.MaxMetadataEntries)
        {
            errors.Add(new OpenAiValidationError("metadata_too_many", $"metadata must contain no more than {_options.RequestLimits.MaxMetadataEntries} entries.", "metadata"));
        }

        foreach (var (key, value) in metadata)
        {
            if (key.Length > _options.RequestLimits.MaxMetadataKeyLength)
            {
                errors.Add(new OpenAiValidationError("metadata_key_too_long", "metadata keys exceed the maximum allowed length.", "metadata"));
            }

            if (MetadataValueToString(value).Length > _options.RequestLimits.MaxMetadataValueLength)
            {
                errors.Add(new OpenAiValidationError("metadata_value_too_long", "metadata values exceed the maximum allowed length.", "metadata"));
            }
        }
    }

    private static string MetadataValueToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        _ => element.GetRawText()
    };
}

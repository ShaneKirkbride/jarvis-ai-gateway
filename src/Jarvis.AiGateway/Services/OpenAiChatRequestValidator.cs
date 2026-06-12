using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IOpenAiChatRequestValidator
{
    OpenAiChatValidationResult Validate(HttpContext httpContext, OpenAiChatCompletionRequest? request, GatewayModel? resolvedModel = null);
    Task<OpenAiChatRequestValidationResult> ValidateAsync(OpenAiChatCompletionRequest? request, CancellationToken cancellationToken);
}

public sealed record OpenAiValidationError(string Code, string Message, string? Field = null);

public sealed record OpenAiChatValidationResult(bool IsValid, IReadOnlyList<OpenAiValidationError> Errors, AiChatRequest? AiRequest)
{
    public static OpenAiChatValidationResult Success(AiChatRequest aiRequest) => new(true, [], aiRequest);
    public static OpenAiChatValidationResult Failure(params OpenAiValidationError[] errors) => new(false, errors, null);

    public string? Message => Errors.FirstOrDefault()?.Message;
    public string? Code => Errors.FirstOrDefault()?.Code;
}

public sealed record OpenAiChatRequestValidationResult(bool IsValid, string? Message = null, string? Code = null)
{
    public static OpenAiChatRequestValidationResult Success { get; } = new(true);
    public static OpenAiChatRequestValidationResult Failure(string message, string code) => new(false, message, code);
}

public sealed class OpenAiChatRequestValidator : IOpenAiChatRequestValidator
{
    private static readonly HashSet<string> SupportedRoles = new(StringComparer.OrdinalIgnoreCase) { "system", "user", "assistant" };
    private readonly GatewayOptions _options;
    private readonly IModelRegistry? modelRegistry;

    public OpenAiChatRequestValidator(IOptions<GatewayOptions> options)
    {
        _options = options.Value;
    }

    public OpenAiChatRequestValidator(IOptions<GatewayOptions> options, IModelRegistry modelRegistry)
        : this(options)
    {
        this.modelRegistry = modelRegistry;
    }

    public async Task<OpenAiChatRequestValidationResult> ValidateAsync(OpenAiChatCompletionRequest? request, CancellationToken cancellationToken)
    {
        GatewayModel? model = null;
        if (!string.IsNullOrWhiteSpace(request?.Model))
        {
            model = modelRegistry is null ? null : await modelRegistry.FindChatModelAsync(request.Model, cancellationToken);
        }

        var result = Validate(new DefaultHttpContext(), request, model);
        return result.IsValid
            ? OpenAiChatRequestValidationResult.Success
            : OpenAiChatRequestValidationResult.Failure(result.Message ?? "Invalid request.", result.Code ?? "invalid_request");
    }

    public OpenAiChatValidationResult Validate(HttpContext httpContext, OpenAiChatCompletionRequest? request, GatewayModel? resolvedModel = null)
    {
        if (request is null)
        {
            return Fail("request_required", "Request body is required.", null);
        }

        var errors = new List<OpenAiValidationError>();
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            errors.Add(new("model_required", "The 'model' field is required.", "model"));
        }

        var messages = new List<AiMessage>();
        if (request.Messages is null || request.Messages.Count == 0)
        {
            errors.Add(new("messages_required", "The 'messages' field is required and must contain at least one message.", "messages"));
        }
        else
        {
            if (request.Messages.Count > _options.RequestLimits.MaxMessageCount)
            {
                errors.Add(new("too_many_messages", $"The 'messages' field may contain at most {_options.RequestLimits.MaxMessageCount} messages.", "messages"));
            }

            var hasNonSystemMessage = false;
            for (var i = 0; i < request.Messages.Count; i++)
            {
                var message = request.Messages[i];
                if (message is null)
                {
                    errors.Add(new("invalid_message", $"Message at index {i} is required.", $"messages[{i}]"));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(message.Role) || !SupportedRoles.Contains(message.Role))
                {
                    errors.Add(new("unsupported_role", $"Message at index {i} uses unsupported role '{message.Role}'. Supported roles are system, user, and assistant.", $"messages[{i}].role"));
                }
                else if (!message.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                {
                    hasNonSystemMessage = true;
                }

                if (!message.TryGetTextContent(out var text, out var error))
                {
                    var code = message.Content.ValueKind == JsonValueKind.Array ? "unsupported_content_part" : "unsupported_content";
                    errors.Add(new(code, error ?? $"Message at index {i} contains unsupported content.", $"messages[{i}].content"));
                }
                else
                {
                    messages.Add(new AiMessage(message.Role, text));
                }
            }

            if (!hasNonSystemMessage)
            {
                errors.Add(new("conversation_message_required", "At least one user or assistant message is required.", "messages"));
            }
        }

        AddHeaderErrors(httpContext, errors);

        if (request.Temperature is < 0 || request.Temperature < _options.RequestValidation.MinimumTemperature || request.Temperature > _options.RequestValidation.MaximumTemperature)
        {
            errors.Add(new("temperature_out_of_range", $"The 'temperature' field must be between {_options.RequestValidation.MinimumTemperature} and {_options.RequestValidation.MaximumTemperature}.", "temperature"));
        }

        if (request.TopP is < 0 or > 1)
        {
            errors.Add(new("top_p_out_of_range", "The 'top_p' field must be between 0 and 1.", "top_p"));
        }

        if (request.MaxTokens is <= 0)
        {
            errors.Add(new("max_tokens_out_of_range", "The 'max_tokens' field must be positive when supplied.", "max_tokens"));
            errors.Add(new("max_tokens_invalid", "The 'max_tokens' field must be positive when supplied.", "max_tokens"));
        }

        if (resolvedModel is not null && request.MaxTokens is { } maxTokens && maxTokens > resolvedModel.MaxOutputTokens)
        {
            errors.Add(new("max_tokens_exceeds_model_limit", $"The 'max_tokens' field exceeds the configured maximum of {resolvedModel.MaxOutputTokens} for model '{resolvedModel.Id}'.", "max_tokens"));
        }

        if (request.Stream && !_options.Streaming.FallbackToNonStreaming)
        {
            errors.Add(new("streaming_disabled", "Streaming responses are disabled until Bedrock ConverseStream is implemented. Send stream=false.", "stream"));
        }

        var stopSequences = GetStopSequences(request.Stop, errors);
        var metadata = GetMetadata(request.Metadata, errors);

        if (errors.Count > 0)
        {
            return new OpenAiChatValidationResult(false, errors, null);
        }

        var aiRequest = new AiChatRequest(
            request.Model,
            messages,
            new AiGenerationOptions(request.Temperature, request.TopP, request.MaxTokens, stopSequences),
            metadata,
            request.Stream);

        return OpenAiChatValidationResult.Success(aiRequest);
    }

    private void AddHeaderErrors(HttpContext context, List<OpenAiValidationError> errors)
    {
        var names = new[]
        {
            "X-Correlation-Id",
            "X-Request-Id",
            "X-Jarvis-Workspace-Id",
            "X-Jarvis-Data-Label",
            "X-Jarvis-Itar-Mode",
            _options.ServiceApiKeyHeader,
            _options.UserTokenHeader
        };

        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (context.Request.Headers.TryGetValue(name, out var value) && value.ToString().Length > _options.RequestLimits.MaxGatewayHeaderLength)
            {
                errors.Add(new("header_too_large", $"Gateway header '{name}' exceeds the configured length limit.", name));
            }
        }
    }

    private IReadOnlyList<string> GetStopSequences(JsonElement? stop, List<OpenAiValidationError> errors)
    {
        var values = new List<string>();
        if (stop is null || stop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return values;
        }

        if (stop.Value.ValueKind == JsonValueKind.String)
        {
            values.Add(stop.Value.GetString() ?? string.Empty);
        }
        else if (stop.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in stop.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new("invalid_stop", "The 'stop' field must be a string or an array of strings.", "stop"));
                    return values;
                }

                values.Add(item.GetString() ?? string.Empty);
            }
        }
        else
        {
            errors.Add(new("invalid_stop", "The 'stop' field must be a string or an array of strings.", "stop"));
            return values;
        }

        var maxCount = Math.Min(_options.RequestValidation.MaxStopSequences, _options.RequestLimits.MaxStopSequenceCount);
        if (values.Count > maxCount)
        {
            errors.Add(new("too_many_stop_sequences", $"The 'stop' field may contain at most {maxCount} sequences.", "stop"));
            errors.Add(new("stop_too_many", $"The 'stop' field may contain at most {maxCount} sequences.", "stop"));
        }

        var maxLength = Math.Min(_options.RequestValidation.MaxStopSequenceCharacters, _options.RequestLimits.MaxStopSequenceLength);
        if (values.Any(s => s.Length > maxLength))
        {
            errors.Add(new("stop_sequence_too_long", $"Each stop sequence must be at most {maxLength} characters.", "stop"));
        }

        return values;
    }

    private IReadOnlyDictionary<string, string> GetMetadata(Dictionary<string, JsonElement>? metadata, List<OpenAiValidationError> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (metadata is null)
        {
            return result;
        }

        if (metadata.Count > _options.RequestLimits.MaxMetadataEntries)
        {
            errors.Add(new("too_many_metadata_entries", $"The 'metadata' field may contain at most {_options.RequestLimits.MaxMetadataEntries} entries.", "metadata"));
            errors.Add(new("metadata_too_many", $"The 'metadata' field may contain at most {_options.RequestLimits.MaxMetadataEntries} entries.", "metadata"));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata).Length;
        if (bytes > _options.RequestValidation.MaxMetadataBytes)
        {
            errors.Add(new("metadata_too_large", $"The 'metadata' field must be at most {_options.RequestValidation.MaxMetadataBytes} bytes.", "metadata"));
        }

        foreach (var (key, element) in metadata)
        {
            if (key.Length > _options.RequestLimits.MaxMetadataKeyLength)
            {
                errors.Add(new("metadata_key_too_large", $"Metadata keys must be at most {_options.RequestLimits.MaxMetadataKeyLength} characters.", "metadata"));
                continue;
            }

            var value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
                JsonValueKind.Null => string.Empty,
                _ => null
            };

            if (value is null)
            {
                errors.Add(new("unsupported_metadata_value", "Metadata values must be scalar strings, numbers, booleans, or null.", "metadata"));
                continue;
            }

            if (value.Length > _options.RequestLimits.MaxMetadataValueLength)
            {
                errors.Add(new("metadata_value_too_large", $"Metadata values must be at most {_options.RequestLimits.MaxMetadataValueLength} characters.", "metadata"));
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static OpenAiChatValidationResult Fail(string code, string message, string? field) =>
        OpenAiChatValidationResult.Failure(new OpenAiValidationError(code, message, field));
}

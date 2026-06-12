using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IOpenAiChatRequestValidator
{
    Task<OpenAiChatRequestValidationResult> ValidateAsync(OpenAiChatCompletionRequest? request, CancellationToken cancellationToken);
}

public sealed record OpenAiChatRequestValidationResult(bool IsValid, string? Message = null, string? Code = null)
{
    public static OpenAiChatRequestValidationResult Success { get; } = new(true);
    public static OpenAiChatRequestValidationResult Failure(string message, string code) => new(false, message, code);
}

public sealed class OpenAiChatRequestValidator(IOptions<GatewayOptions> options, IModelRegistry modelRegistry) : IOpenAiChatRequestValidator
{
    private static readonly HashSet<string> SupportedRoles = new(StringComparer.OrdinalIgnoreCase) { "system", "user", "assistant" };
    private readonly GatewayOptions _options = options.Value;

    public async Task<OpenAiChatRequestValidationResult> ValidateAsync(OpenAiChatCompletionRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Fail("Request body is required.", "request_required");
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return Fail("The 'model' field is required.", "model_required");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            return Fail("The 'messages' field is required and must contain at least one message.", "messages_required");
        }

        var hasConversationMessage = false;
        for (var i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            if (string.IsNullOrWhiteSpace(message.Role) || !SupportedRoles.Contains(message.Role))
            {
                return Fail($"Message at index {i} has unsupported role '{message.Role}'. Supported roles are system, user, and assistant.", "unsupported_role");
            }

            if (!message.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                hasConversationMessage = true;
            }

            var contentResult = ValidateContent(message.Content, i);
            if (!contentResult.IsValid)
            {
                return contentResult;
            }
        }

        if (!hasConversationMessage)
        {
            return Fail("At least one user or assistant message is required.", "conversation_message_required");
        }

        if (request.Temperature is < 0 || request.Temperature < _options.RequestValidation.MinimumTemperature || request.Temperature > _options.RequestValidation.MaximumTemperature)
        {
            return Fail($"The 'temperature' field must be between {_options.RequestValidation.MinimumTemperature} and {_options.RequestValidation.MaximumTemperature}.", "temperature_out_of_range");
        }

        if (request.TopP is < 0 or > 1)
        {
            return Fail("The 'top_p' field must be between 0 and 1.", "top_p_out_of_range");
        }

        if (request.MaxTokens is <= 0)
        {
            return Fail("The 'max_tokens' field must be positive when supplied.", "max_tokens_out_of_range");
        }

        var stopResult = ValidateStop(request.Stop);
        if (!stopResult.IsValid)
        {
            return stopResult;
        }

        var metadataResult = ValidateMetadata(request.Metadata);
        if (!metadataResult.IsValid)
        {
            return metadataResult;
        }

        if (request.Stream && !_options.Streaming.FallbackToNonStreaming)
        {
            return Fail("Streaming responses are disabled until Bedrock ConverseStream is implemented. Send stream=false.", "streaming_disabled");
        }

        if (request.MaxTokens is { } maxTokens)
        {
            var model = await modelRegistry.FindChatModelAsync(request.Model, cancellationToken);
            if (model is not null && maxTokens > model.MaxOutputTokens)
            {
                return Fail($"The 'max_tokens' field exceeds the configured maximum of {model.MaxOutputTokens} for model '{model.Id}'.", "max_tokens_exceeds_model_limit");
            }
        }

        return OpenAiChatRequestValidationResult.Success;
    }

    private OpenAiChatRequestValidationResult ValidateContent(JsonElement content, int index)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return OpenAiChatRequestValidationResult.Success;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return Fail($"Message at index {index} must use string content or an array of supported text content parts.", "unsupported_content");
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                return Fail($"Message at index {index} contains a non-object content part.", "unsupported_content_part");
            }

            if (!part.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                return Fail($"Message at index {index} contains a content part without a string type.", "unsupported_content_part");
            }

            var typeValue = type.GetString();
            if (!string.Equals(typeValue, "text", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(typeValue, "input_text", StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"Message at index {index} contains unsupported content part type '{typeValue}'. Only text parts are supported.", "unsupported_content_part");
            }

            if (!part.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            {
                return Fail($"Message at index {index} contains a text content part without string text.", "unsupported_content_part");
            }
        }

        return OpenAiChatRequestValidationResult.Success;
    }

    private OpenAiChatRequestValidationResult ValidateStop(JsonElement? stop)
    {
        if (stop is null || stop.Value.ValueKind == JsonValueKind.Null || stop.Value.ValueKind == JsonValueKind.Undefined)
        {
            return OpenAiChatRequestValidationResult.Success;
        }

        var sequences = new List<string>();
        if (stop.Value.ValueKind == JsonValueKind.String)
        {
            sequences.Add(stop.Value.GetString() ?? string.Empty);
        }
        else if (stop.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in stop.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return Fail("The 'stop' field must be a string or an array of strings.", "invalid_stop");
                }

                sequences.Add(item.GetString() ?? string.Empty);
            }
        }
        else
        {
            return Fail("The 'stop' field must be a string or an array of strings.", "invalid_stop");
        }

        if (sequences.Count > _options.RequestValidation.MaxStopSequences)
        {
            return Fail($"The 'stop' field may contain at most {_options.RequestValidation.MaxStopSequences} sequences.", "too_many_stop_sequences");
        }

        if (sequences.Any(s => s.Length > _options.RequestValidation.MaxStopSequenceCharacters))
        {
            return Fail($"Each stop sequence must be at most {_options.RequestValidation.MaxStopSequenceCharacters} characters.", "stop_sequence_too_long");
        }

        return OpenAiChatRequestValidationResult.Success;
    }

    private OpenAiChatRequestValidationResult ValidateMetadata(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null)
        {
            return OpenAiChatRequestValidationResult.Success;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata).Length;
        return bytes <= _options.RequestValidation.MaxMetadataBytes
            ? OpenAiChatRequestValidationResult.Success
            : Fail($"The 'metadata' field must be at most {_options.RequestValidation.MaxMetadataBytes} bytes.", "metadata_too_large");
    }

    private static OpenAiChatRequestValidationResult Fail(string message, string code) => OpenAiChatRequestValidationResult.Failure(message, code);
}

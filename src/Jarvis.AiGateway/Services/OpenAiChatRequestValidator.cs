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

        // Tool/function calling (Phase 1): capability-gated. Detect any tool usage up front so the
        // text-only default stays unchanged for non-tool requests, and tools fail closed otherwise.
        var usesTools = (request.Tools is { Count: > 0 })
            || (request.Messages?.Any(m => m is not null &&
                    (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase) || m.ToolCalls is { Count: > 0 })) ?? false);

        if (usesTools && request.Stream)
        {
            errors.Add(new("tool_streaming_not_supported", "Streaming is not supported for requests that include tools in this gateway version. Retry with \"stream\": false.", "stream"));
        }

        // The capability gate only applies once the model is resolved (the orchestrator's second
        // validation pass). The first pass (model unknown) builds the request so policy can run.
        if (usesTools && resolvedModel is not null && !resolvedModel.SupportsTools)
        {
            errors.Add(new("tools_not_supported", $"Model '{resolvedModel.Id}' does not support tool/function calling.", "tools"));
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

                var isToolMessage = string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase);
                var isAssistantToolCall = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) && message.ToolCalls is { Count: > 0 };

                if (string.IsNullOrWhiteSpace(message.Role) || (!SupportedRoles.Contains(message.Role) && !isToolMessage))
                {
                    errors.Add(new("unsupported_role", $"Message at index {i} uses unsupported role '{message.Role}'. Supported roles are system, user, assistant, and tool.", $"messages[{i}].role"));
                    continue;
                }

                if (!message.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
                {
                    hasNonSystemMessage = true;
                }

                if (isToolMessage)
                {
                    // Tool result returned by the client. Treated as untrusted content (redacted /
                    // ITAR-routed downstream like any prompt). Requires the originating call id.
                    if (string.IsNullOrWhiteSpace(message.ToolCallId))
                    {
                        errors.Add(new("tool_call_id_required", $"Tool message at index {i} requires 'tool_call_id'.", $"messages[{i}].tool_call_id"));
                        continue;
                    }

                    messages.Add(new AiMessage("tool", ExtractRawText(message), ToolCallId: message.ToolCallId));
                    continue;
                }

                if (isAssistantToolCall)
                {
                    if (!TryBuildToolCalls(message.ToolCalls!, i, errors, out var toolCalls))
                    {
                        continue;
                    }

                    messages.Add(new AiMessage("assistant", ExtractOptionalText(message), ToolCalls: toolCalls));
                    continue;
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
            errors.Add(new("max_tokens_invalid", "The 'max_tokens' field must be positive when supplied.", "max_tokens"));
        }

        if (request.MaxCompletionTokens is <= 0)
        {
            errors.Add(new("max_completion_tokens_invalid", "The 'max_completion_tokens' field must be positive when supplied.", "max_completion_tokens"));
        }

        if (resolvedModel is not null && request.MaxTokens is { } maxTokens && maxTokens > resolvedModel.MaxOutputTokens)
        {
            errors.Add(new("max_tokens_exceeds_model_limit", $"The 'max_tokens' field exceeds the configured maximum of {resolvedModel.MaxOutputTokens} for model '{resolvedModel.Id}'.", "max_tokens"));
        }

        if (resolvedModel is not null && request.MaxCompletionTokens is { } maxCompletionTokens && maxCompletionTokens > resolvedModel.MaxOutputTokens)
        {
            errors.Add(new("max_completion_tokens_exceeds_model_limit", $"The 'max_completion_tokens' field exceeds the configured maximum of {resolvedModel.MaxOutputTokens} for model '{resolvedModel.Id}'.", "max_completion_tokens"));
        }

        // Note: stream=true is no longer rejected here. Streaming is fully supported; whether a
        // streamed request is served as SSE, falls back to a single non-streaming completion, or
        // is rejected is decided in ChatCompletionOrchestrator after policy authorization, so a
        // policy denial still returns its 403 before any stream is opened.

        var stopSequences = GetStopSequences(request.Stop, errors);
        var metadata = GetMetadata(request.Metadata, errors);
        var tools = BuildTools(request.Tools, errors);
        var toolChoice = ParseToolChoice(request.ToolChoice, tools, errors);

        if (errors.Count > 0)
        {
            return new OpenAiChatValidationResult(false, errors, null);
        }

        var aiRequest = new AiChatRequest(
            request.Model,
            messages,
            new AiGenerationOptions(request.Temperature, request.TopP, request.MaxTokens, stopSequences, request.MaxCompletionTokens),
            metadata,
            request.Stream,
            tools,
            toolChoice);

        return OpenAiChatValidationResult.Success(aiRequest);
    }

    private static string ExtractRawText(OpenAiMessage message) => message.Content.ValueKind switch
    {
        JsonValueKind.String => message.Content.GetString() ?? string.Empty,
        JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
        // A tool result sent as a JSON object/array is preserved as its raw JSON text (and is
        // redacted/ITAR-routed downstream like any other content).
        _ => message.Content.GetRawText()
    };

    private static string ExtractOptionalText(OpenAiMessage message) => message.Content.ValueKind switch
    {
        JsonValueKind.String => message.Content.GetString() ?? string.Empty,
        _ => string.Empty
    };

    private static bool TryBuildToolCalls(List<OpenAiToolCall> toolCalls, int messageIndex, List<OpenAiValidationError> errors, out List<AiToolCall> built)
    {
        built = [];
        foreach (var call in toolCalls)
        {
            if (call is null || string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Function?.Name))
            {
                errors.Add(new("invalid_tool_call", $"Assistant message at index {messageIndex} has a tool_call missing id or function.name.", $"messages[{messageIndex}].tool_calls"));
                return false;
            }

            built.Add(new AiToolCall(call.Id, call.Function.Name, call.Function.Arguments ?? string.Empty));
        }

        return true;
    }

    private List<AiToolDefinition>? BuildTools(List<OpenAiTool>? tools, List<OpenAiValidationError> errors)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        if (tools.Count > _options.RequestValidation.MaxTools)
        {
            errors.Add(new("too_many_tools", $"The 'tools' field may contain at most {_options.RequestValidation.MaxTools} tools.", "tools"));
            return null;
        }

        var built = new List<AiToolDefinition>(tools.Count);
        for (var i = 0; i < tools.Count; i++)
        {
            var function = tools[i]?.Function;
            if (function is null || string.IsNullOrWhiteSpace(function.Name))
            {
                errors.Add(new("invalid_tool", $"tools[{i}] must define function.name.", $"tools[{i}].function.name"));
                continue;
            }

            var parameters = function.Parameters ?? EmptyObjectSchema;
            if (parameters.ValueKind != JsonValueKind.Undefined &&
                parameters.GetRawText().Length > _options.RequestValidation.MaxToolSchemaBytes)
            {
                errors.Add(new("tool_schema_too_large", $"tools[{i}] parameters schema exceeds {_options.RequestValidation.MaxToolSchemaBytes} bytes.", $"tools[{i}].function.parameters"));
                continue;
            }

            built.Add(new AiToolDefinition(function.Name, function.Description, parameters));
        }

        return built.Count > 0 ? built : null;
    }

    private static AiToolChoice? ParseToolChoice(JsonElement? toolChoice, List<AiToolDefinition>? tools, List<OpenAiValidationError> errors)
    {
        if (toolChoice is not { } choice || choice.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (choice.ValueKind == JsonValueKind.String)
        {
            var mode = choice.GetString();
            if (mode is "auto" or "none" or "required")
            {
                return new AiToolChoice(mode);
            }

            errors.Add(new("invalid_tool_choice", "The 'tool_choice' string must be one of: auto, none, required.", "tool_choice"));
            return null;
        }

        if (choice.ValueKind == JsonValueKind.Object &&
            choice.TryGetProperty("function", out var fn) &&
            fn.ValueKind == JsonValueKind.Object &&
            fn.TryGetProperty("name", out var nameEl) &&
            nameEl.ValueKind == JsonValueKind.String)
        {
            var name = nameEl.GetString()!;
            if (tools is null || !tools.Any(t => t.Name.Equals(name, StringComparison.Ordinal)))
            {
                errors.Add(new("tool_choice_unknown_function", $"tool_choice references function '{name}' which is not declared in 'tools'.", "tool_choice"));
                return null;
            }

            return new AiToolChoice("function", name);
        }

        errors.Add(new("invalid_tool_choice", "The 'tool_choice' field must be a string (auto|none|required) or a {type:function, function:{name}} object.", "tool_choice"));
        return null;
    }

    private static readonly JsonElement EmptyObjectSchema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();

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

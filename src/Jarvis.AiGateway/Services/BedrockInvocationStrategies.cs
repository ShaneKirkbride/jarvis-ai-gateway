using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IBedrockInvocationStrategy
{
    string Name { get; }
    bool CanHandle(GatewayModel model, AiChatRequest request);
    Task<AiChatResult> InvokeAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Streaming counterpart to <see cref="IBedrockInvocationStrategy"/>.  Yields provider-neutral
/// <see cref="AiChatStreamEvent"/>s as the model produces tokens.  Implementations must apply
/// the same inbound redaction, request mapping, and endpoint configuration as the non-streaming
/// path — only the transport (ConverseStream vs Converse) differs.
/// </summary>
public interface IBedrockStreamingStrategy
{
    string Name { get; }
    bool CanHandle(GatewayModel model, AiChatRequest request);
    IAsyncEnumerable<AiChatStreamEvent> StreamAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        CancellationToken cancellationToken);
}

public interface IInvokeModelPayloadAdapter
{
    bool CanHandle(GatewayModel model);
    string BuildRequestBody(GatewayModel model, AiChatRequest request, RequestContext context);
    AiChatResult ParseResponseBody(GatewayModel model, string responseBody, RequestContext context);
}

public sealed class BedrockConverseInvocationStrategy(
    IAmazonBedrockRuntime bedrockRuntime,
    IContentRedactor redactor,
    IOptions<GatewayOptions> gatewayOptions,
    ILogger<BedrockConverseInvocationStrategy> logger) : IBedrockInvocationStrategy
{
    private readonly GatewayOptions _options = gatewayOptions.Value;
    public string Name => "converse";

    public bool CanHandle(GatewayModel model, AiChatRequest request)
    {
        return model.SupportsConverse && !model.InvocationMode.Equals("InvokeModel", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<AiChatResult> InvokeAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var components = ConverseRequestComponents.Build(model, request, context, _options, redactor);
        if (components.Messages.Count == 0)
        {
            throw new InvalidOperationException("At least one non-system message is required.");
        }

        var bedrockRequest = new ConverseRequest
        {
            ModelId = model.BedrockModelId,
            Messages = components.Messages,
            InferenceConfig = components.Inference,
            RequestMetadata = components.Metadata
        };

        if (components.System.Count > 0)
        {
            bedrockRequest.System = components.System;
        }

        if (components.ToolConfig is not null)
        {
            bedrockRequest.ToolConfig = components.ToolConfig;
        }

        logger.LogInformation("Invoking Bedrock Converse for model {ModelId} ({Alias}).", model.BedrockModelId, model.Alias);
        var providerStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await bedrockRuntime.ConverseAsync(bedrockRequest, cancellationToken);
        providerStopwatch.Stop();

        var contentBlocks = response.Output?.Message?.Content ?? [];
        var responseText = string.Concat(contentBlocks.Where(c => !string.IsNullOrEmpty(c.Text)).Select(c => c.Text));
        var toolCalls = contentBlocks
            .Where(c => c.ToolUse is not null)
            .Select(c => new AiToolCall(c.ToolUse.ToolUseId, c.ToolUse.Name, BedrockToolMapper.DocumentToJson(c.ToolUse.Input)))
            .ToList();

        return new AiChatResult(
            responseText,
            new Jarvis.AiGateway.Models.TokenUsage(response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0, response.Usage?.TotalTokens ?? 0),
            response.StopReason?.Value ?? "stop",
            new ProviderInvocationMetadata(model.ProviderName, Name, providerStopwatch.ElapsedMilliseconds, response.ResponseMetadata?.RequestId),
            toolCalls.Count > 0 ? toolCalls : null);
    }
}

/// <summary>
/// Shared builder so the Converse and ConverseStream paths produce byte-identical request
/// components (messages, system blocks, inference config, request metadata) and apply the
/// same inbound redaction rules.  Keeping this in one place guarantees a streamed request is
/// mapped exactly like its non-streaming equivalent.
/// </summary>
internal static class ConverseRequestComponents
{
    public readonly record struct Result(
        List<SystemContentBlock> System,
        List<Message> Messages,
        InferenceConfiguration Inference,
        Dictionary<string, string> Metadata,
        ToolConfiguration? ToolConfig);

    public static Result Build(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        GatewayOptions options,
        IContentRedactor redactor)
    {
        var systemBlocks = new List<SystemContentBlock>();
        var messages = new List<Message>();

        // Redact if configured globally OR if ITAR mode is active.
        // ITAR requests must always be redacted before leaving the gateway boundary,
        // regardless of the Gateway:Redaction:RedactBeforeBedrock setting.
        var redactForThisRequest = options.Redaction.RedactBeforeBedrock
            || context.ItarMode
            || DataLabelClassifier.IsItar(context.DataLabel);

        string Redact(string value) => redactForThisRequest ? redactor.Redact(value).Text : value;

        foreach (var input in request.Messages)
        {
            if (input.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                var systemText = Redact(input.Content);
                if (!string.IsNullOrWhiteSpace(systemText)) systemBlocks.Add(new SystemContentBlock { Text = systemText });
                continue;
            }

            // Tool result returned by the client. Bedrock carries tool results in a USER-role
            // message; the content is redacted like any untrusted prompt content.
            if (input.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new Message
                {
                    Role = ConversationRole.User,
                    Content = [new ContentBlock { ToolResult = new ToolResultBlock
                    {
                        ToolUseId = input.ToolCallId,
                        Content = [new ToolResultContentBlock { Text = Redact(input.Content) }]
                    } }]
                });
                continue;
            }

            // Assistant turn that requested tool calls → one ToolUse content block per call.
            if (input.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) && input.ToolCalls is { Count: > 0 })
            {
                var content = new List<ContentBlock>();
                var assistantText = Redact(input.Content);
                if (!string.IsNullOrWhiteSpace(assistantText)) content.Add(new ContentBlock { Text = assistantText });
                foreach (var call in input.ToolCalls)
                {
                    content.Add(new ContentBlock { ToolUse = new ToolUseBlock
                    {
                        ToolUseId = call.Id,
                        Name = call.Name,
                        Input = BedrockToolMapper.ArgumentsToDocument(Redact(call.ArgumentsJson))
                    } });
                }
                messages.Add(new Message { Role = ConversationRole.Assistant, Content = content });
                continue;
            }

            var text = Redact(input.Content);
            if (string.IsNullOrWhiteSpace(text)) continue;

            messages.Add(new Message
            {
                Role = input.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? ConversationRole.Assistant : ConversationRole.User,
                Content = [new ContentBlock { Text = text }]
            });
        }

        var inferenceConfig = new InferenceConfiguration
        {
            MaxTokens = Math.Min(request.Options.MaxTokens ?? model.MaxOutputTokens, model.MaxOutputTokens),
            Temperature = request.Options.Temperature ?? 0.2F,
            TopP = request.Options.TopP ?? 0.9F
        };

        foreach (var stop in request.Options.StopSequences)
        {
            inferenceConfig.StopSequences.Add(stop);
        }

        var metadata = new Dictionary<string, string>
        {
            ["gateway"] = options.ServiceName,
            ["environment"] = options.EnvironmentName,
            ["requestId"] = context.RequestId,
            ["correlationId"] = context.CorrelationId,
            ["workspaceId"] = context.WorkspaceId,
            ["dataLabel"] = context.DataLabel,
            ["modelAlias"] = model.Alias
        };

        var toolConfig = BedrockToolMapper.BuildToolConfig(request, redactor, redactForThisRequest);

        return new Result(systemBlocks, messages, inferenceConfig, metadata, toolConfig);
    }
}

/// <summary>
/// Maps the provider-neutral tool model to/from Bedrock Converse types (toolConfig / toolUse /
/// toolResult), including JSON ⇄ <see cref="Document"/> conversion for tool input/output.  Lives
/// in the (coverage-excluded) Bedrock adapter because it depends on the AWS SDK types; the
/// provider-neutral request/response mapping it feeds is tested through the orchestrator path.
/// </summary>
internal static class BedrockToolMapper
{
    public static ToolConfiguration? BuildToolConfig(AiChatRequest request, IContentRedactor redactor, bool redact)
    {
        if (request.Tools is not { Count: > 0 } tools)
        {
            return null;
        }

        var config = new ToolConfiguration { Tools = [] };
        foreach (var tool in tools)
        {
            config.Tools.Add(new Tool
            {
                ToolSpec = new ToolSpecification
                {
                    Name = tool.Name,
                    Description = tool.Description is null ? null : (redact ? redactor.Redact(tool.Description).Text : tool.Description),
                    // JSON-schema parameters are structural and passed through unchanged.
                    InputSchema = new ToolInputSchema { Json = JsonElementToDocument(tool.Parameters) }
                }
            });
        }

        var toolChoice = MapToolChoice(request.ToolChoice);
        if (toolChoice is not null)
        {
            config.ToolChoice = toolChoice;
        }

        return config;
    }

    // Bedrock has no explicit "none" choice; for none/unset we omit ToolChoice and let the model decide.
    private static ToolChoice? MapToolChoice(AiToolChoice? choice) => choice?.Mode switch
    {
        "required" => new ToolChoice { Any = new AnyToolChoice() },
        "function" when !string.IsNullOrWhiteSpace(choice.FunctionName) => new ToolChoice { Tool = new SpecificToolChoice { Name = choice.FunctionName } },
        "auto" => new ToolChoice { Auto = new AutoToolChoice() },
        _ => null
    };

    public static Document ArgumentsToDocument(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Document(new Dictionary<string, Document>());
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            return JsonElementToDocument(doc.RootElement);
        }
        catch (JsonException)
        {
            // A non-JSON arguments string is wrapped as an empty object rather than failing the call.
            return new Document(new Dictionary<string, Document>());
        }
    }

    public static Document JsonElementToDocument(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new Document(element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToDocument(p.Value))),
        JsonValueKind.Array => new Document(element.EnumerateArray().Select(JsonElementToDocument).ToList()),
        JsonValueKind.String => new Document(element.GetString()),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? new Document(l) : new Document(element.GetDouble()),
        JsonValueKind.True => new Document(true),
        JsonValueKind.False => new Document(false),
        _ => new Document()
    };

    public static string DocumentToJson(Document document)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            WriteDocument(writer, document);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDocument(System.Text.Json.Utf8JsonWriter writer, Document document)
    {
        if (document.IsDictionary())
        {
            writer.WriteStartObject();
            foreach (var pair in document.AsDictionary())
            {
                writer.WritePropertyName(pair.Key);
                WriteDocument(writer, pair.Value);
            }
            writer.WriteEndObject();
        }
        else if (document.IsList())
        {
            writer.WriteStartArray();
            foreach (var item in document.AsList())
            {
                WriteDocument(writer, item);
            }
            writer.WriteEndArray();
        }
        else if (document.IsString())
        {
            writer.WriteStringValue(document.AsString());
        }
        else if (document.IsBool())
        {
            writer.WriteBooleanValue(document.AsBool());
        }
        else if (document.IsInt())
        {
            writer.WriteNumberValue(document.AsInt());
        }
        else if (document.IsLong())
        {
            writer.WriteNumberValue(document.AsLong());
        }
        else if (document.IsDouble())
        {
            writer.WriteNumberValue(document.AsDouble());
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>
/// Streaming counterpart of <see cref="BedrockConverseInvocationStrategy"/>.  Calls Bedrock
/// ConverseStream and adapts the provider event stream (message start, content-block deltas,
/// message stop, metadata) into provider-neutral <see cref="AiChatStreamEvent"/>s, surfacing
/// only text deltas plus a single terminal completion event.  Uses the shared
/// <see cref="ConverseRequestComponents"/> builder so the request is mapped identically to the
/// non-streaming Converse path, including ITAR/global inbound redaction.
/// </summary>
public sealed class BedrockConverseStreamInvocationStrategy(
    IAmazonBedrockRuntime bedrockRuntime,
    IContentRedactor redactor,
    IOptions<GatewayOptions> gatewayOptions,
    ILogger<BedrockConverseStreamInvocationStrategy> logger) : IBedrockStreamingStrategy
{
    private readonly GatewayOptions _options = gatewayOptions.Value;
    public string Name => "converse-stream";

    public bool CanHandle(GatewayModel model, AiChatRequest request)
    {
        return model.SupportsConverse && !model.InvocationMode.Equals("InvokeModel", StringComparison.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<AiChatStreamEvent> StreamAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var components = ConverseRequestComponents.Build(model, request, context, _options, redactor);
        if (components.Messages.Count == 0)
        {
            throw new InvalidOperationException("At least one non-system message is required.");
        }

        var bedrockRequest = new ConverseStreamRequest
        {
            ModelId = model.BedrockModelId,
            Messages = components.Messages,
            InferenceConfig = components.Inference,
            RequestMetadata = components.Metadata
        };

        if (components.System.Count > 0)
        {
            bedrockRequest.System = components.System;
        }

        logger.LogInformation("Invoking Bedrock ConverseStream for model {ModelId} ({Alias}).", model.BedrockModelId, model.Alias);
        var response = await bedrockRuntime.ConverseStreamAsync(bedrockRequest, cancellationToken);

        var finishReason = "stop";
        Jarvis.AiGateway.Models.TokenUsage? usage = null;

        await foreach (var streamEvent in response.Stream.WithCancellation(cancellationToken))
        {
            switch (streamEvent)
            {
                case ContentBlockDeltaEvent delta when !string.IsNullOrEmpty(delta.Delta?.Text):
                    yield return new AiChatTextDeltaEvent(delta.Delta.Text);
                    break;
                case MessageStopEvent stop when !string.IsNullOrWhiteSpace(stop.StopReason?.Value):
                    finishReason = stop.StopReason.Value;
                    break;
                case ConverseStreamMetadataEvent metadata when metadata.Usage is not null:
                    usage = new Jarvis.AiGateway.Models.TokenUsage(
                        metadata.Usage.InputTokens ?? 0,
                        metadata.Usage.OutputTokens ?? 0,
                        metadata.Usage.TotalTokens ?? 0);
                    break;
            }
        }

        yield return new AiChatCompletionEvent(finishReason, usage);
    }
}

public sealed class BedrockInvokeModelTextInvocationStrategy : IBedrockInvocationStrategy
{
    public const string UnsupportedAdapterMessage = "Model is discovered but not supported by this gateway invocation adapter yet.";
    public string Name => "invoke-model";

    private readonly IAmazonBedrockRuntime _bedrockRuntime;
    private readonly IReadOnlyList<IInvokeModelPayloadAdapter> _adapters;
    private readonly IContentRedactor _redactor;
    private readonly GatewayOptions _options;
    private readonly ILogger<BedrockInvokeModelTextInvocationStrategy> _logger;

    public BedrockInvokeModelTextInvocationStrategy(
        IAmazonBedrockRuntime bedrockRuntime,
        IEnumerable<IInvokeModelPayloadAdapter> adapters,
        IContentRedactor redactor,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<BedrockInvokeModelTextInvocationStrategy> logger)
    {
        _bedrockRuntime = bedrockRuntime;
        _adapters = adapters.ToList();
        _redactor = redactor;
        _options = gatewayOptions.Value;
        _logger = logger;
    }

    public bool CanHandle(GatewayModel model, AiChatRequest request)
    {
        if (model.InvocationMode.Equals("Converse", StringComparison.OrdinalIgnoreCase)) return false;
        return _adapters.Any(a => a.CanHandle(model));
    }

    public async Task<AiChatResult> InvokeAsync(
        GatewayModel model,
        AiChatRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        var adapter = _adapters.FirstOrDefault(a => a.CanHandle(model));
        if (adapter is null)
        {
            throw new NotSupportedException(UnsupportedAdapterMessage);
        }

        // Redact if configured globally OR if ITAR mode is active (same logic as Converse path).
        var redactForThisRequest = _options.Redaction.RedactBeforeBedrock
            || context.ItarMode
            || DataLabelClassifier.IsItar(context.DataLabel);

        var effectiveRequest = redactForThisRequest ? RedactMessages(request) : request;

        _logger.LogInformation("Invoking Bedrock InvokeModel for model {ModelId} ({Alias}) with adapter {AdapterType}.", model.BedrockModelId, model.Alias, adapter.GetType().Name);
        var body = adapter.BuildRequestBody(model, effectiveRequest, context);
        var invokeRequest = new InvokeModelRequest
        {
            ModelId = model.BedrockModelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(body))
        };

        var providerStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await _bedrockRuntime.InvokeModelAsync(invokeRequest, cancellationToken);
        providerStopwatch.Stop();
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        var responseBody = await reader.ReadToEndAsync(cancellationToken);

        AiChatResult rawResult;
        try
        {
            rawResult = adapter.ParseResponseBody(model, responseBody, context);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse InvokeModel response for model {ModelId}.", model.BedrockModelId);
            throw new ProviderResponseParseException($"Bedrock response could not be parsed for model '{model.Alias}'.", ex);
        }

        return rawResult with
        {
            ProviderMetadata = rawResult.ProviderMetadata with
            {
                InvocationStrategy = Name,
                LatencyMs = providerStopwatch.ElapsedMilliseconds,
                ProviderRequestId = response.ResponseMetadata?.RequestId
            }
        };
    }

    private AiChatRequest RedactMessages(AiChatRequest request)
    {
        var redactedMessages = request.Messages
            .Select(m => m with { Content = _redactor.Redact(m.Content).Text })
            .ToList();
        return request with { Messages = redactedMessages };
    }
}

public static class OpenAiRequestHelpers
{
    public static string Prompt(AiChatRequest request)
    {
        var builder = new StringBuilder();
        foreach (var message in request.Messages)
        {
            var role = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role;
            builder.Append(role).Append(": ").AppendLine(message.Content);
        }

        builder.Append("assistant: ");
        return builder.ToString();
    }

    public static IReadOnlyList<string> GetStopSequences(AiChatRequest request) => request.Options.StopSequences;
}

using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
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
        var systemBlocks = new List<SystemContentBlock>();
        var messages = new List<Message>();

        foreach (var input in request.Messages)
        {
            var text = input.Content;
            if (_options.Redaction.RedactBeforeBedrock)
            {
                text = redactor.Redact(text).Text;
            }

            if (string.IsNullOrWhiteSpace(text)) continue;

            if (input.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                systemBlocks.Add(new SystemContentBlock { Text = text });
                continue;
            }

            messages.Add(new Message
            {
                Role = input.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? ConversationRole.Assistant : ConversationRole.User,
                Content = [new ContentBlock { Text = text }]
            });
        }

        if (messages.Count == 0)
        {
            throw new InvalidOperationException("At least one non-system message is required.");
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

        var bedrockRequest = new ConverseRequest
        {
            ModelId = model.BedrockModelId,
            Messages = messages,
            InferenceConfig = inferenceConfig,
            RequestMetadata = new Dictionary<string, string>
            {
                ["gateway"] = _options.ServiceName,
                ["environment"] = _options.EnvironmentName,
                ["requestId"] = context.RequestId,
                ["correlationId"] = context.CorrelationId,
                ["workspaceId"] = context.WorkspaceId,
                ["dataLabel"] = context.DataLabel,
                ["modelAlias"] = model.Alias
            }
        };

        if (systemBlocks.Count > 0)
        {
            bedrockRequest.System = systemBlocks;
        }

        logger.LogInformation("Invoking Bedrock Converse for model {ModelId} ({Alias}).", model.BedrockModelId, model.Alias);
        var providerStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await bedrockRuntime.ConverseAsync(bedrockRequest, cancellationToken);
        providerStopwatch.Stop();
        var responseText = response.Output?.Message?.Content?
            .Where(c => !string.IsNullOrEmpty(c.Text))
            .Select(c => c.Text)
            .DefaultIfEmpty(string.Empty)
            .Aggregate((a, b) => a + b) ?? string.Empty;

        return new AiChatResult(responseText, new Jarvis.AiGateway.Models.TokenUsage(response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0, response.Usage?.TotalTokens ?? 0), response.StopReason?.Value ?? "stop", new ProviderInvocationMetadata(model.ProviderName, Name, providerStopwatch.ElapsedMilliseconds, response.ResponseMetadata?.RequestId));
    }
}

public sealed class BedrockInvokeModelTextInvocationStrategy(
    IAmazonBedrockRuntime bedrockRuntime,
    IEnumerable<IInvokeModelPayloadAdapter> adapters,
    ILogger<BedrockInvokeModelTextInvocationStrategy> logger) : IBedrockInvocationStrategy
{
    public const string UnsupportedAdapterMessage = "Model is discovered but not supported by this gateway invocation adapter yet.";
    public string Name => "invoke-model";
    private readonly IReadOnlyList<IInvokeModelPayloadAdapter> _adapters = adapters.ToList();

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

        logger.LogInformation("Invoking Bedrock InvokeModel for model {ModelId} ({Alias}) with adapter {AdapterType}.", model.BedrockModelId, model.Alias, adapter.GetType().Name);
        var body = adapter.BuildRequestBody(model, request, context);
        var invokeRequest = new InvokeModelRequest
        {
            ModelId = model.BedrockModelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(body))
        };

        var providerStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await bedrockRuntime.InvokeModelAsync(invokeRequest, cancellationToken);
        providerStopwatch.Stop();
        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        var responseBody = await reader.ReadToEndAsync(cancellationToken);
        var result = adapter.ParseResponseBody(model, responseBody, context);
        return result with { ProviderMetadata = result.ProviderMetadata with { InvocationStrategy = Name, LatencyMs = providerStopwatch.ElapsedMilliseconds, ProviderRequestId = response.ResponseMetadata?.RequestId } };
    }
}

public static class OpenAiResponseFactory
{
    public static OpenAiChatCompletionResponse FromResult(string modelId, AiChatResult result) =>
        FromText(modelId, result.Text, result.Usage.PromptTokens, result.Usage.CompletionTokens, result.Usage.TotalTokens, result.FinishReason);

    public static OpenAiChatCompletionResponse FromText(string modelId, string text, int inputTokens = 0, int outputTokens = 0, int totalTokens = 0, string finishReason = "stop") => new()
    {
        Model = modelId,
        Choices =
        [
            new OpenAiChoice
            {
                Index = 0,
                Message = new OpenAiAssistantMessage { Role = "assistant", Content = text },
                FinishReason = NormalizeFinishReason(finishReason)
            }
        ],
        Usage = new OpenAiUsage
        {
            PromptTokens = inputTokens,
            CompletionTokens = outputTokens,
            TotalTokens = totalTokens == 0 ? inputTokens + outputTokens : totalTokens
        }
    };

    private static string NormalizeFinishReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "stop";
        return reason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase) || reason.Equals("length", StringComparison.OrdinalIgnoreCase) ? "length" : "stop";
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

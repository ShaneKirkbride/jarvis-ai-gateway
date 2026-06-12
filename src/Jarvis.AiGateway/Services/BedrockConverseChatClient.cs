using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IBedrockChatClient
{
    Task<BedrockChatResult> CompleteAsync(
        OpenAiChatCompletionRequest request,
        ModelRouteOptions model,
        RequestContext requestContext,
        CancellationToken cancellationToken);
}

public sealed class BedrockConverseChatClient(
    IAmazonBedrockRuntime bedrockRuntime,
    IContentRedactor redactor,
    IOptions<GatewayOptions> gatewayOptions,
    ILogger<BedrockConverseChatClient> logger) : IBedrockChatClient
{
    private readonly GatewayOptions _options = gatewayOptions.Value;

    public async Task<BedrockChatResult> CompleteAsync(
        OpenAiChatCompletionRequest request,
        ModelRouteOptions model,
        RequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var systemBlocks = new List<SystemContentBlock>();
        var messages = new List<Message>();

        foreach (var input in request.Messages)
        {
            var text = input.GetTextContent();
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

            var role = input.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                ? ConversationRole.Assistant
                : ConversationRole.User;

            messages.Add(new Message
            {
                Role = role,
                Content = [new ContentBlock { Text = text }]
            });
        }

        if (messages.Count == 0)
        {
            throw new InvalidOperationException("At least one non-system message is required.");
        }

        var maxTokens = Math.Min(request.MaxTokens ?? model.MaxOutputTokens, model.MaxOutputTokens);

        var bedrockRequest = new ConverseRequest
        {
            ModelId = model.BedrockModelId,
            Messages = messages,
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens = maxTokens,
                Temperature = request.Temperature ?? 0.2F,
                TopP = request.TopP ?? 0.9F
            },
            RequestMetadata = new Dictionary<string, string>
            {
                ["gateway"] = _options.ServiceName,
                ["environment"] = _options.EnvironmentName,
                ["requestId"] = requestContext.RequestId,
                ["correlationId"] = requestContext.CorrelationId,
                ["workspaceId"] = requestContext.WorkspaceId,
                ["dataLabel"] = requestContext.DataLabel,
                ["modelAlias"] = model.Alias
            }
        };

        if (systemBlocks.Count > 0)
        {
            bedrockRequest.System = systemBlocks;
        }

        logger.LogDebug("Invoking Bedrock model alias {ModelAlias} with resolved model ID {BedrockModelId}.", model.Alias, model.BedrockModelId);
        var response = await bedrockRuntime.ConverseAsync(bedrockRequest, cancellationToken);

        var text = response.Output?.Message?.Content?
            .Where(c => !string.IsNullOrEmpty(c.Text))
            .Select(c => c.Text)
            .DefaultIfEmpty(string.Empty)
            .Aggregate((a, b) => a + b) ?? string.Empty;

        return new BedrockChatResult(
            Text: text,
            InputTokens: response.Usage?.InputTokens ?? 0,
            OutputTokens: response.Usage?.OutputTokens ?? 0,
            TotalTokens: response.Usage?.TotalTokens ?? 0,
            StopReason: response.StopReason?.Value ?? "stop");
    }
}

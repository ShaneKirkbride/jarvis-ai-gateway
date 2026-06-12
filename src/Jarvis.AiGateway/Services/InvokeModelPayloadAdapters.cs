using System.Text.Json;
using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

public sealed class AmazonTitanTextInvokeModelPayloadAdapter : IInvokeModelPayloadAdapter
{
    public bool CanHandle(GatewayModel model) =>
        model.BedrockModelId.Contains("amazon.titan-text", StringComparison.OrdinalIgnoreCase) ||
        (model.ProviderName.Equals("Amazon", StringComparison.OrdinalIgnoreCase) && model.BedrockModelId.Contains("titan", StringComparison.OrdinalIgnoreCase) && model.HasTextOutput);

    public string BuildRequestBody(GatewayModel model, AiChatRequest request, RequestContext context)
    {
        var body = new
        {
            inputText = OpenAiRequestHelpers.Prompt(request),
            textGenerationConfig = new
            {
                maxTokenCount = Math.Min(request.Options.MaxTokens ?? model.MaxOutputTokens, model.MaxOutputTokens),
                temperature = request.Options.Temperature ?? 0.2F,
                topP = request.Options.TopP ?? 0.9F,
                stopSequences = OpenAiRequestHelpers.GetStopSequences(request)
            }
        };
        return JsonSerializer.Serialize(body, JsonDefaults.Options);
    }

    public AiChatResult ParseResponseBody(GatewayModel model, string responseBody, RequestContext context)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        var text = string.Empty;
        var outputTokens = 0;
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
        {
            var first = results[0];
            if (first.TryGetProperty("outputText", out var outputText)) text = outputText.GetString() ?? string.Empty;
            if (first.TryGetProperty("tokenCount", out var tokenCount)) outputTokens = tokenCount.GetInt32();
        }

        var inputTokens = root.TryGetProperty("inputTextTokenCount", out var inputTokenCount) ? inputTokenCount.GetInt32() : 0;
        return new AiChatResult(text, new TokenUsage(inputTokens, outputTokens, inputTokens + outputTokens), "stop", new ProviderInvocationMetadata(model.ProviderName, "invoke-model", 0));
    }
}

public sealed class MetaLlamaInvokeModelPayloadAdapter : IInvokeModelPayloadAdapter
{
    public bool CanHandle(GatewayModel model) =>
        model.BedrockModelId.Contains("meta.llama", StringComparison.OrdinalIgnoreCase) ||
        model.ProviderName.Equals("Meta", StringComparison.OrdinalIgnoreCase);

    public string BuildRequestBody(GatewayModel model, AiChatRequest request, RequestContext context)
    {
        var body = new
        {
            prompt = OpenAiRequestHelpers.Prompt(request),
            max_gen_len = Math.Min(request.Options.MaxTokens ?? model.MaxOutputTokens, model.MaxOutputTokens),
            temperature = request.Options.Temperature ?? 0.2F,
            top_p = request.Options.TopP ?? 0.9F
        };
        return JsonSerializer.Serialize(body, JsonDefaults.Options);
    }

    public AiChatResult ParseResponseBody(GatewayModel model, string responseBody, RequestContext context)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        var text = root.TryGetProperty("generation", out var generation) ? generation.GetString() ?? string.Empty : string.Empty;
        var promptTokens = root.TryGetProperty("prompt_token_count", out var prompt) ? prompt.GetInt32() : 0;
        var generationTokens = root.TryGetProperty("generation_token_count", out var output) ? output.GetInt32() : 0;
        var stopReason = root.TryGetProperty("stop_reason", out var stop) ? stop.GetString() ?? "stop" : "stop";
        return new AiChatResult(text, new TokenUsage(promptTokens, generationTokens, promptTokens + generationTokens), stopReason, new ProviderInvocationMetadata(model.ProviderName, "invoke-model", 0));
    }
}

public sealed class MistralInvokeModelPayloadAdapter : IInvokeModelPayloadAdapter
{
    public bool CanHandle(GatewayModel model) =>
        model.BedrockModelId.Contains("mistral.", StringComparison.OrdinalIgnoreCase) ||
        model.ProviderName.Equals("Mistral AI", StringComparison.OrdinalIgnoreCase) ||
        model.ProviderName.Equals("Mistral", StringComparison.OrdinalIgnoreCase);

    public string BuildRequestBody(GatewayModel model, AiChatRequest request, RequestContext context)
    {
        var body = new
        {
            prompt = OpenAiRequestHelpers.Prompt(request),
            max_tokens = Math.Min(request.Options.MaxTokens ?? model.MaxOutputTokens, model.MaxOutputTokens),
            temperature = request.Options.Temperature ?? 0.2F,
            top_p = request.Options.TopP ?? 0.9F,
            stop = OpenAiRequestHelpers.GetStopSequences(request)
        };
        return JsonSerializer.Serialize(body, JsonDefaults.Options);
    }

    public AiChatResult ParseResponseBody(GatewayModel model, string responseBody, RequestContext context)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        var text = string.Empty;
        var stopReason = "stop";
        if (root.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array && outputs.GetArrayLength() > 0)
        {
            var first = outputs[0];
            if (first.TryGetProperty("text", out var textElement)) text = textElement.GetString() ?? string.Empty;
            if (first.TryGetProperty("stop_reason", out var stopElement)) stopReason = stopElement.GetString() ?? "stop";
        }

        return new AiChatResult(text, new TokenUsage(0, 0, 0), stopReason, new ProviderInvocationMetadata(model.ProviderName, "invoke-model", 0));
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}

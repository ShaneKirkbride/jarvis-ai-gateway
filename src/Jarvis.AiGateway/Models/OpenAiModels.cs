using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.AiGateway.Models;

public sealed class OpenAiChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OpenAiMessage> Messages { get; set; } = [];

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}

public sealed class OpenAiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }

    public bool TryGetTextContent(out string text, out string? error)
    {
        text = string.Empty;
        error = null;

        if (Content.ValueKind == JsonValueKind.String)
        {
            text = Content.GetString() ?? string.Empty;
            return true;
        }

        if (Content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            var index = 0;
            foreach (var item in Content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    error = $"messages content part at index {index} must be an object with type 'text'.";
                    return false;
                }

                if (!item.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String ||
                    (!string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(typeElement.GetString(), "input_text", StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"unsupported content part type at index {index}; only text content is supported.";
                    return false;
                }

                if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    parts.Add(textElement.GetString() ?? string.Empty);
                }
                else
                {
                    error = $"text content part at index {index} must include a string text property.";
                    return false;
                }

                index++;
            }

            text = string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
            return true;
        }

        if (Content.ValueKind == JsonValueKind.Object &&
            Content.TryGetProperty("type", out var objectType) &&
            objectType.ValueKind == JsonValueKind.String &&
            (string.Equals(objectType.GetString(), "text", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(objectType.GetString(), "input_text", StringComparison.OrdinalIgnoreCase)) &&
            Content.TryGetProperty("text", out var objectText) &&
            objectText.ValueKind == JsonValueKind.String)
        {
            text = objectText.GetString() ?? string.Empty;
            return true;
        }

        error = "message content must be a string or OpenAI text content parts; image, tool, audio, and arbitrary object content are not supported.";
        return false;
    }

    public string GetTextContent()
    {
        if (TryGetTextContent(out var text, out _))
        {
            return text;
        }

        throw new InvalidOperationException("Unsupported OpenAI message content. Validate the request before extracting text.");
    }
}

public sealed class OpenAiModelListResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("data")]
    public List<OpenAiModelInfo> Data { get; set; } = [];
}

public sealed class OpenAiModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    [JsonPropertyName("created")]
    public long Created { get; set; } = 0;

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = "jarvis-ai-gateway";
}

public sealed class OpenAiChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = $"chatcmpl-{Guid.NewGuid():N}";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<OpenAiChoice> Choices { get; set; } = [];

    [JsonPropertyName("usage")]
    public OpenAiUsage? Usage { get; set; }
}

public sealed class OpenAiChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public OpenAiAssistantMessage Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; } = "stop";
}

public sealed class OpenAiAssistantMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public sealed class OpenAiUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

public sealed class OpenAiErrorResponse
{
    [JsonPropertyName("error")]
    public OpenAiError Error { get; set; } = new();

    public static OpenAiErrorResponse Create(string message, string type = "invalid_request_error", string? code = null) => new()
    {
        Error = new OpenAiError { Message = message, Type = type, Code = code }
    };
}

public sealed class OpenAiError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "invalid_request_error";

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

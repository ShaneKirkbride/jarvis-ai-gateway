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

    public string GetTextContent()
    {
        if (Content.ValueKind == JsonValueKind.String)
        {
            return Content.GetString() ?? string.Empty;
        }

        if (Content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in Content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(text.GetString() ?? string.Empty);
                    }
                    else if (item.TryGetProperty("content", out var nestedContent) && nestedContent.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(nestedContent.GetString() ?? string.Empty);
                    }
                }
            }

            return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        if (Content.ValueKind == JsonValueKind.Object &&
            Content.TryGetProperty("text", out var objectText) &&
            objectText.ValueKind == JsonValueKind.String)
        {
            return objectText.GetString() ?? string.Empty;
        }

        return Content.GetRawText();
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

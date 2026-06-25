using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.AiGateway.Models;

// Anthropic Messages API wire shapes (Phase 4). The gateway accepts these on POST /v1/messages and
// maps them onto the existing provider-neutral chat pipeline, then maps the result back to the
// Anthropic response shape. Text content only for now (multimodal is deferred / policy-gated).

public sealed class AnthropicMessagesRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<AnthropicMessage> Messages { get; set; } = [];

    // Optional system prompt: a string or an array of text blocks.
    [JsonPropertyName("system")]
    public JsonElement? System { get; set; }

    // Anthropic requires max_tokens.
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("stop_sequences")]
    public List<string>? StopSequences { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }
}

public sealed class AnthropicMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    // A string or an array of content blocks (text blocks supported; image/tool blocks deferred).
    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }
}

public sealed class AnthropicMessagesResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = $"msg_{Guid.NewGuid():N}";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "message";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "assistant";

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public List<AnthropicContentBlock> Content { get; set; } = [];

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("stop_sequence")]
    public string? StopSequence { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage Usage { get; set; } = new();
}

public sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }
}

public sealed class AnthropicErrorResponse
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "error";

    [JsonPropertyName("error")]
    public AnthropicError Error { get; set; } = new();

    public static AnthropicErrorResponse Create(string errorType, string message) => new()
    {
        Error = new AnthropicError { Type = errorType, Message = message }
    };
}

public sealed class AnthropicError
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "invalid_request_error";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

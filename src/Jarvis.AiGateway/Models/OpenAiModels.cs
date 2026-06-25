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

    // GPT-5.x deployments reject max_tokens and require this instead. Preserved when supplied.
    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }

    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; set; }

    // Tool/function calling (Phase 1). Capability-gated: accepted only for models advertising
    // supports_tools; rejected otherwise (the text-only default is unchanged for non-tool models).
    [JsonPropertyName("tools")]
    public List<OpenAiTool>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    public JsonElement? ToolChoice { get; set; }
}

public sealed class OpenAiMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }

    // Present on an assistant message that requested tool calls.
    [JsonPropertyName("tool_calls")]
    public List<OpenAiToolCall>? ToolCalls { get; set; }

    // Present on a "tool" role message carrying a tool result back to the model.
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

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

    // ── Additive capability metadata ────────────────────────────────────────────────
    // Extra fields beyond the OpenAI model object. OpenAI/Open WebUI clients ignore unknown
    // fields, so the base list shape is preserved; IDE clients can read these to adapt UX.

    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; set; }

    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("supports_chat")]
    public bool SupportsChat { get; set; }

    [JsonPropertyName("supports_streaming")]
    public bool SupportsStreaming { get; set; }

    [JsonPropertyName("supports_tools")]
    public bool SupportsTools { get; set; }

    [JsonPropertyName("supports_embeddings")]
    public bool SupportsEmbeddings { get; set; }

    [JsonPropertyName("supports_fim")]
    public bool SupportsFim { get; set; }

    [JsonPropertyName("supports_vision")]
    public bool SupportsVision { get; set; }

    [JsonPropertyName("context_window")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextWindow { get; set; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("approved_for_itar")]
    public bool ApprovedForItar { get; set; }
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

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAiToolCall>? ToolCalls { get; set; }
}

// ── Tool/function calling wire types ────────────────────────────────────────────
public sealed class OpenAiTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionDefinition Function { get; set; } = new();
}

public sealed class OpenAiFunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; set; }
}

public sealed class OpenAiToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionCall Function { get; set; } = new();
}

public sealed class OpenAiFunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // OpenAI sends arguments as a JSON-encoded string (not a JSON object).
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
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

// ── Embeddings (Phase 2) ────────────────────────────────────────────────────────
public sealed class OpenAiEmbeddingsRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    // OpenAI accepts a string or an array of strings (token-array inputs are not supported).
    [JsonPropertyName("input")]
    public JsonElement Input { get; set; }

    [JsonPropertyName("dimensions")]
    public int? Dimensions { get; set; }

    [JsonPropertyName("encoding_format")]
    public string? EncodingFormat { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }
}

public sealed class OpenAiEmbeddingsResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("data")]
    public List<OpenAiEmbeddingData> Data { get; set; } = [];

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    public OpenAiEmbeddingsUsage Usage { get; set; } = new();
}

public sealed class OpenAiEmbeddingData
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "embedding";

    [JsonPropertyName("embedding")]
    public IReadOnlyList<float> Embedding { get; set; } = [];

    [JsonPropertyName("index")]
    public int Index { get; set; }
}

public sealed class OpenAiEmbeddingsUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

// ── Completions / FIM (Phase 3) ─────────────────────────────────────────────────
public sealed class OpenAiCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    // OpenAI accepts a string or an array of strings (token-array prompts are not supported).
    [JsonPropertyName("prompt")]
    public JsonElement Prompt { get; set; }

    // Fill-in-the-middle: text after the insertion point.
    [JsonPropertyName("suffix")]
    public string? Suffix { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public float? TopP { get; set; }

    [JsonPropertyName("stop")]
    public JsonElement? Stop { get; set; }

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }
}

public sealed class OpenAiCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = $"cmpl-{Guid.NewGuid():N}";

    [JsonPropertyName("object")]
    public string Object { get; set; } = "text_completion";

    [JsonPropertyName("created")]
    public long Created { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<OpenAiCompletionChoice> Choices { get; set; } = [];

    [JsonPropertyName("usage")]
    public OpenAiUsage? Usage { get; set; }
}

public sealed class OpenAiCompletionChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; } = "stop";

    [JsonPropertyName("logprobs")]
    public object? Logprobs { get; set; }
}

// ── Streaming (SSE) chunk shapes ────────────────────────────────────────────────
// Mirrors OpenAI's chat.completion.chunk schema.  Each chunk is serialized and written as
// a `data: {json}\n\n` Server-Sent Event.  Open WebUI depends on this shape; do not rename
// or remove these fields.  finish_reason is intentionally written even when null so the
// shape matches OpenAI exactly; role/content are omitted when null to keep the empty
// terminal delta as `{}`.
public sealed class OpenAiChatCompletionChunk
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion.chunk";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<OpenAiChunkChoice> Choices { get; set; } = [];
}

public sealed class OpenAiChunkChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("delta")]
    public OpenAiChunkDelta Delta { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public sealed class OpenAiChunkDelta
{
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }
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

using System.Text.Json;

namespace Jarvis.AiGateway.Models;

public sealed record AiChatRequest(
    string Model,
    IReadOnlyList<AiMessage> Messages,
    AiGenerationOptions Options,
    IReadOnlyDictionary<string, string> Metadata,
    bool Stream,
    // Tool/function calling (Phase 1). Null when the request declares no tools. Additive optional
    // parameters keep existing positional construction valid.
    IReadOnlyList<AiToolDefinition>? Tools = null,
    AiToolChoice? ToolChoice = null);

public sealed record AiMessage(
    string Role,
    string Content,
    // Populated on an assistant message that requested tool calls.
    IReadOnlyList<AiToolCall>? ToolCalls = null,
    // Populated on a "tool" role message carrying a tool result back to the model.
    string? ToolCallId = null);

/// <summary>A tool/function the model may call. <see cref="Parameters"/> is a JSON Schema element.</summary>
public sealed record AiToolDefinition(string Name, string? Description, JsonElement Parameters);

/// <summary>A tool call requested by the model (or replayed by the client). Arguments are raw JSON.</summary>
public sealed record AiToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Provider-neutral tool-choice directive. Mode is auto|none|required|function.</summary>
public sealed record AiToolChoice(string Mode, string? FunctionName = null);

public sealed record AiGenerationOptions(
    float? Temperature,
    float? TopP,
    int? MaxTokens,
    IReadOnlyList<string> StopSequences,
    // Provider-neutral "completion token budget" preserved from an inbound max_completion_tokens.
    // Optional last parameter so existing positional call sites are unaffected.
    int? MaxCompletionTokens = null);

public sealed record AiChatResult(
    string Text,
    TokenUsage Usage,
    string FinishReason,
    ProviderInvocationMetadata ProviderMetadata,
    // Tool calls the model requested this turn (empty/null when none).
    IReadOnlyList<AiToolCall>? ToolCalls = null);

public sealed record TokenUsage(int PromptTokens, int CompletionTokens, int TotalTokens);

public sealed record ProviderInvocationMetadata(
    string Provider,
    string InvocationStrategy,
    long LatencyMs,
    string? ProviderRequestId = null);

// ── Streaming events ────────────────────────────────────────────────────────────
// Provider-neutral events emitted by an IBedrockStreamingStrategy and translated into
// OpenAI-compatible SSE chunks by OpenAiSseStreamResult.  Only text deltas and a single
// terminal completion event are surfaced; provider-specific event framing (message start,
// content-block start/stop, etc.) is collapsed away inside the strategy.
public abstract record AiChatStreamEvent;

/// <summary>A single incremental text fragment from the model.</summary>
public sealed record AiChatTextDeltaEvent(string Text) : AiChatStreamEvent;

/// <summary>
/// Terminal event for the stream.  <paramref name="FinishReason"/> is the raw provider stop
/// reason (mapped to an OpenAI finish_reason downstream).  <paramref name="Usage"/> is null
/// when the provider did not report token counts for the stream.
/// </summary>
public sealed record AiChatCompletionEvent(string FinishReason, TokenUsage? Usage) : AiChatStreamEvent;

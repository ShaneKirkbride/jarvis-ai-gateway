namespace Jarvis.AiGateway.Models;

public sealed record AiChatRequest(
    string Model,
    IReadOnlyList<AiMessage> Messages,
    AiGenerationOptions Options,
    IReadOnlyDictionary<string, string> Metadata,
    bool Stream);

public sealed record AiMessage(string Role, string Content);

public sealed record AiGenerationOptions(
    float? Temperature,
    float? TopP,
    int? MaxTokens,
    IReadOnlyList<string> StopSequences);

public sealed record AiChatResult(
    string Text,
    TokenUsage Usage,
    string FinishReason,
    ProviderInvocationMetadata ProviderMetadata);

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

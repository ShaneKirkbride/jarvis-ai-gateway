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

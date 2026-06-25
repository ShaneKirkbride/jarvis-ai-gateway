namespace Jarvis.AiGateway.Models;

/// <summary>
/// Provider-neutral text-completion / fill-in-the-middle request (Phase 3).  <see cref="Suffix"/>
/// is the text that should follow the insertion point (FIM); null for a plain completion.
/// </summary>
public sealed record AiCompletionRequest(
    string Model,
    string Prompt,
    string? Suffix,
    int? MaxTokens,
    float? Temperature,
    float? TopP,
    IReadOnlyList<string> StopSequences);

public sealed record AiCompletionResult(
    string Text,
    TokenUsage Usage,
    string FinishReason,
    ProviderInvocationMetadata ProviderMetadata);

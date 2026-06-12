using Jarvis.AiGateway.Options;

namespace Jarvis.AiGateway.Models;

public sealed record UserContext(
    string Subject,
    string Email,
    IReadOnlySet<string> Groups,
    IReadOnlyDictionary<string, string> Claims);

public sealed record RequestContext(
    string RequestId,
    string CorrelationId,
    string WorkspaceId,
    string DataLabel,
    bool ItarMode);

public sealed record PolicyDecision(
    bool Allowed,
    string Reason,
    ModelRouteOptions? Model);

public sealed record RedactionResult(string Text, int RedactionCount);

public sealed record BedrockChatResult(
    string Text,
    int InputTokens,
    int OutputTokens,
    int TotalTokens,
    string StopReason);

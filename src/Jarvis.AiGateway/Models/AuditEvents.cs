namespace Jarvis.AiGateway.Models;

public sealed class GatewayAuditEvent
{
    public string EventType { get; set; } = "AI_MODEL_INVOCATION";
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string RequestId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string Decision { get; set; } = "UNKNOWN";
    public string? DenyReason { get; set; }
    public string UserSubject { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string[] UserGroups { get; set; } = [];
    public string WorkspaceId { get; set; } = string.Empty;
    public string DataLabel { get; set; } = "NON_ITAR";
    public bool ItarMode { get; set; }
    public string RequestedModelAlias { get; set; } = string.Empty;
    public string? ResolvedBedrockModelId { get; set; }
    public string Provider { get; set; } = "aws-bedrock";
    public string? InvocationStrategy { get; set; }
    public bool? SupportsConverse { get; set; }
    public bool? StreamingSupported { get; set; }
    public string? PolicyDecision { get; set; }
    public int? TokenEstimate { get; set; }
    public string Region { get; set; } = string.Empty;
    public int PromptCharacters { get; set; }
    public int RedactionCount { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public long LatencyMs { get; set; }
    public string EndpointMode { get; set; } = "regional-or-vpce-configured";
}

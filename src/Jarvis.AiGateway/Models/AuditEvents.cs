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
    public string? PolicyRuleId { get; set; }
    public int? TokenEstimate { get; set; }
    public string Region { get; set; } = string.Empty;
    public int PromptCharacters { get; set; }
    public int RedactionCount { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
    public long LatencyMs { get; set; }
    public long? ProviderLatencyMs { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorCategory { get; set; }
    public string? ProviderRequestId { get; set; }
    public string EndpointMode { get; set; } = "regional-or-vpce-configured";

    // How the caller authenticated (e.g. "developer_api_key", "service_api_key") and, for a
    // developer key, its key id. Never the raw key. Null for legacy JWT/broker users.
    public string? AuthType { get; set; }
    public string? ApiKeyId { get; set; }

    // Tool/function calling (Phase 1): counts + function names only. Never tool arguments/results.
    public int? ToolsOffered { get; set; }
    public int? ToolCallsReturned { get; set; }
    public string[]? ToolCallNames { get; set; }

    // Embeddings (Phase 2): number of inputs only — never the input text.
    public int? EmbeddingInputCount { get; set; }
}

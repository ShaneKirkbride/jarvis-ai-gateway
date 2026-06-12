using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

public interface IAuditLogger
{
    void Write(GatewayAuditEvent auditEvent);
}

public sealed class AuditLogger(ILogger<AuditLogger> logger) : IAuditLogger
{
    public void Write(GatewayAuditEvent auditEvent)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["audit.event_type"] = auditEvent.EventType,
            ["audit.request_id"] = auditEvent.RequestId,
            ["audit.correlation_id"] = auditEvent.CorrelationId,
            ["audit.user_subject"] = auditEvent.UserSubject,
            ["audit.user_email"] = auditEvent.UserEmail,
            ["audit.user_groups"] = auditEvent.UserGroups,
            ["audit.workspace_id"] = auditEvent.WorkspaceId,
            ["audit.data_label"] = auditEvent.DataLabel,
            ["audit.itar_mode"] = auditEvent.ItarMode,
            ["audit.requested_model_alias"] = auditEvent.RequestedModelAlias,
            ["audit.resolved_bedrock_model_id"] = auditEvent.ResolvedBedrockModelId,
            ["audit.provider"] = auditEvent.Provider,
            ["audit.invocation_strategy"] = auditEvent.InvocationStrategy,
            ["audit.policy_rule_id"] = auditEvent.PolicyRuleId,
            ["audit.decision"] = auditEvent.Decision,
            ["audit.deny_reason"] = auditEvent.DenyReason,
            ["audit.input_tokens"] = auditEvent.InputTokens,
            ["audit.output_tokens"] = auditEvent.OutputTokens,
            ["audit.total_tokens"] = auditEvent.TotalTokens,
            ["audit.gateway_latency_ms"] = auditEvent.LatencyMs,
            ["audit.provider_latency_ms"] = auditEvent.ProviderLatencyMs,
            ["audit.endpoint_mode"] = auditEvent.EndpointMode,
            ["audit.redaction_count"] = auditEvent.RedactionCount,
            ["audit.error_type"] = auditEvent.ErrorType,
            ["audit.error_category"] = auditEvent.ErrorCategory,
            ["audit.provider_request_id"] = auditEvent.ProviderRequestId
        });

        logger.LogInformation(
            "AI_GATEWAY_AUDIT decision={Decision} rule={PolicyRuleId} model={RequestedModelAlias} provider={Provider} latency_ms={LatencyMs}",
            auditEvent.Decision,
            auditEvent.PolicyRuleId,
            auditEvent.RequestedModelAlias,
            auditEvent.Provider,
            auditEvent.LatencyMs);
    }
}

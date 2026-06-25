using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

public interface IAuditLogger
{
    void Write(GatewayAuditEvent auditEvent);

    /// <summary>
    /// Emit a structured identity-resolution audit event.  All identifiers in the payload
    /// must be either hashed (canonical subject, Entra oid) or naturally non-sensitive
    /// (group object IDs, email domain).  Raw tokens, signing keys, prompts, and full
    /// email addresses must never be passed here.
    /// </summary>
    void WriteIdentity(IdentityAuditEvent auditEvent);
}

/// <summary>
/// Structured identity-resolution event for CloudWatch → SIEM.  See plan §8 for the full
/// catalogue of <see cref="EventName"/> values and the level mapping.
/// </summary>
public sealed record IdentityAuditEvent
{
    public string EventName { get; init; } = "";
    public string Level { get; init; } = "info";
    public string? CorrelationId { get; init; }
    public string? RequestPath { get; init; }
    public string? RequestMethod { get; init; }
    public string? RemoteIp { get; init; }
    public string? HashedSubject { get; init; }
    public string? EmailDomain { get; init; }
    public string? HashedOid { get; init; }
    public int? GroupCount { get; init; }
    public IReadOnlyList<string>? GroupIds { get; init; }
    public string? AssertionKind { get; init; }
    public string? IdentitySource { get; init; }
    public string? FailureReason { get; init; }
    public string? DiagnosticHint { get; init; }
    public string? MtlsObservedSubject { get; init; }
    public IReadOnlyList<string>? MtlsAcceptedSubjects { get; init; }
    public string? PreAuthRateLimitPartition { get; init; }
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
            // policy_decision carries the specific internal rule reason (e.g. the exact ITAR
            // denial cause) even when deny_reason has been replaced with a generic client message.
            ["audit.policy_decision"] = auditEvent.PolicyDecision,
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
            ["audit.provider_request_id"] = auditEvent.ProviderRequestId,
            ["audit.auth_type"] = auditEvent.AuthType,
            ["audit.api_key_id"] = auditEvent.ApiKeyId
        });

        logger.LogInformation(
            "AI_GATEWAY_AUDIT decision={Decision} rule={PolicyRuleId} model={RequestedModelAlias} provider={Provider} latency_ms={LatencyMs}",
            auditEvent.Decision,
            auditEvent.PolicyRuleId,
            auditEvent.RequestedModelAlias,
            auditEvent.Provider,
            auditEvent.LatencyMs);
    }

    public void WriteIdentity(IdentityAuditEvent auditEvent)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["audit.event_type"] = auditEvent.EventName,
            ["audit.correlation_id"] = auditEvent.CorrelationId,
            ["audit.request_path"] = auditEvent.RequestPath,
            ["audit.request_method"] = auditEvent.RequestMethod,
            ["audit.remote_ip"] = auditEvent.RemoteIp,
            ["audit.identity.hashed_subject"] = auditEvent.HashedSubject,
            ["audit.identity.email_domain"] = auditEvent.EmailDomain,
            ["audit.identity.hashed_oid"] = auditEvent.HashedOid,
            ["audit.identity.group_count"] = auditEvent.GroupCount,
            ["audit.identity.group_ids"] = auditEvent.GroupIds,
            ["audit.identity.assertion_kind"] = auditEvent.AssertionKind,
            ["audit.identity.source"] = auditEvent.IdentitySource,
            ["audit.identity.failure_reason"] = auditEvent.FailureReason,
            ["audit.identity.diagnostic_hint"] = auditEvent.DiagnosticHint,
            ["audit.identity.mtls_observed_subject"] = auditEvent.MtlsObservedSubject,
            ["audit.identity.mtls_accepted_subjects"] = auditEvent.MtlsAcceptedSubjects,
            ["audit.identity.preauth_partition"] = auditEvent.PreAuthRateLimitPartition
        });

        // Map the configured event-level string into the right ILogger severity so SIEM rules
        // can key on log level alongside the structured payload.
        switch (auditEvent.Level)
        {
            case "debug":
                logger.LogDebug("AI_GATEWAY_IDENTITY {EventName}", auditEvent.EventName);
                break;
            case "warn":
                logger.LogWarning("AI_GATEWAY_IDENTITY {EventName}", auditEvent.EventName);
                break;
            case "error":
                logger.LogError("AI_GATEWAY_IDENTITY {EventName}", auditEvent.EventName);
                break;
            default:
                logger.LogInformation("AI_GATEWAY_IDENTITY {EventName}", auditEvent.EventName);
                break;
        }
    }
}

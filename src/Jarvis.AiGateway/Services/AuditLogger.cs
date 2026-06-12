using System.Text.Json;
using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

public interface IAuditLogger
{
    void Write(GatewayAuditEvent auditEvent);
}

public sealed class AuditLogger(ILogger<AuditLogger> logger) : IAuditLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public void Write(GatewayAuditEvent auditEvent)
    {
        // Structured JSON goes to stdout/stderr and then CloudWatch Logs under ECS.
        logger.LogInformation("AI_GATEWAY_AUDIT {AuditEvent}", JsonSerializer.Serialize(auditEvent, JsonOptions));
    }
}

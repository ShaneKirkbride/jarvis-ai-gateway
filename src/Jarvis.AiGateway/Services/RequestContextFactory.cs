using System.Text.Json;
using Jarvis.AiGateway.Models;

namespace Jarvis.AiGateway.Services;

public interface IRequestContextFactory
{
    RequestContext Create(HttpContext httpContext, OpenAiChatCompletionRequest? request = null);
}

public sealed class RequestContextFactory : IRequestContextFactory
{
    public RequestContext Create(HttpContext httpContext, OpenAiChatCompletionRequest? request = null)
    {
        var requestId = httpContext.TraceIdentifier;
        var correlationId = GetHeader(httpContext, "X-Correlation-Id")
            ?? GetHeader(httpContext, "X-Request-Id")
            ?? requestId;

        var workspaceId = GetHeader(httpContext, "X-Jarvis-Workspace-Id")
            ?? GetMetadataString(request, "workspace_id")
            ?? GetMetadataString(request, "workspace")
            ?? "unknown";

        var dataLabel = GetHeader(httpContext, "X-Jarvis-Data-Label")
            ?? GetMetadataString(request, "data_label")
            ?? "NON_ITAR";

        var itarHeader = GetHeader(httpContext, "X-Jarvis-Itar-Mode")
            ?? GetMetadataString(request, "itar_mode");

        var itarMode = bool.TryParse(itarHeader, out var parsed) && parsed;
        if (dataLabel.Contains("ITAR", StringComparison.OrdinalIgnoreCase))
        {
            itarMode = true;
        }

        return new RequestContext(requestId, correlationId, workspaceId, dataLabel.ToUpperInvariant(), itarMode);
    }

    private static string? GetHeader(HttpContext context, string name)
    {
        return context.Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : null;
    }

    private static string? GetMetadataString(OpenAiChatCompletionRequest? request, string name)
    {
        if (request?.Metadata is null) return null;
        if (!request.Metadata.TryGetValue(name, out var element)) return null;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }
}

using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Services;

namespace Jarvis.AiGateway.Security;

/// <summary>
/// Fails protected API routes (<c>/v1/*</c>) closed with HTTP 503 when critical configuration is
/// invalid, instead of serving requests against a misconfigured gateway.  Health endpoints
/// (<c>/healthz/*</c>, <c>/readyz</c>) are intentionally not gated so orchestrators can observe
/// liveness/readiness and the operator can see why the gateway is unhealthy.
/// </summary>
public sealed class ConfigHealthGateMiddleware(RequestDelegate next, IConfigHealth configHealth)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/v1") && !configHealth.IsReady)
        {
            var response = OpenAiErrorResponse.Create("Gateway is not ready.", "service_unavailable", ConfigHealth.InvalidCode);
            await Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}

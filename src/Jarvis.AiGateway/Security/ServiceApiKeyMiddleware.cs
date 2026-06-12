using System.Security.Cryptography;
using System.Text;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Security;

public sealed class ServiceApiKeyMiddleware(RequestDelegate next, IOptions<GatewayOptions> options)
{
    private readonly GatewayOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.RequireServiceApiKey)
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/healthz"))
        {
            await next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ServiceApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new { error = "Gateway service API key is required but not configured." });
            return;
        }

        if (!context.Request.Headers.TryGetValue(_options.ServiceApiKeyHeader, out var provided) ||
            !FixedTimeEquals(provided.ToString(), _options.ServiceApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid service-to-service gateway key." });
            return;
        }

        await next(context);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

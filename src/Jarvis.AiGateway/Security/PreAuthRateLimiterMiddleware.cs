using System.Threading.RateLimiting;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Security;

/// <summary>
/// IP- or mTLS-partitioned rate limiter that runs BEFORE the identity broker.  Its purpose
/// is to bound the cost of expensive identity-resolution work (JWT validation, Microsoft
/// Graph calls) against a malicious or misconfigured client that has no valid identity to
/// rate-limit against in the first place.
/// <para>
/// This is intentionally separate from the existing per-user post-auth limiter (the
/// <c>"per-user"</c> policy in <c>Program.cs</c>).  The post-auth limiter cannot partition by
/// <c>sub</c> until identity is established, so without the pre-auth tier a token-less
/// flood would still reach the broker.
/// </para>
/// </summary>
public sealed class PreAuthRateLimiterMiddleware : IAsyncDisposable
{
    private readonly RequestDelegate _next;
    private readonly IGatewayMetrics _metrics;
    private readonly IdentityBrokerOptions _brokerOptions;
    private readonly PreAuthRateLimitOptions _limitOptions;
    private readonly PartitionedRateLimiter<HttpContext> _limiter;

    public PreAuthRateLimiterMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> gatewayOptions,
        IGatewayMetrics metrics)
    {
        _next = next;
        _metrics = metrics;
        _brokerOptions = gatewayOptions.Value.IdentityBroker;
        _limitOptions = _brokerOptions.PreAuthRateLimit;

        var permitLimit = Math.Max(1, _limitOptions.PermitLimit);
        var window = TimeSpan.FromSeconds(Math.Max(1, _limitOptions.WindowSeconds));
        var queueLimit = Math.Max(0, _limitOptions.QueueLimit);

        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var partitionKey = SelectPartitionKey(httpContext);
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = queueLimit,
                AutoReplenishment = true
            });
        });
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // The broker can be disabled at runtime even when the middleware is registered;
        // skip pre-auth rate limiting entirely in that case so the legacy auth path is
        // not penalised by a limiter that exists to protect identity resolution work.
        if (!_brokerOptions.Enabled)
        {
            await _next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/healthz") ||
            context.Request.Path.StartsWithSegments("/readyz"))
        {
            await _next(context);
            return;
        }

        using var lease = await _limiter.AcquireAsync(context, permitCount: 1, context.RequestAborted);
        if (lease.IsAcquired)
        {
            await _next(context);
            return;
        }

        var partition = SelectPartitionKey(context);
        _metrics.RecordIdentityPreAuthRateLimited(partition);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var envelope = OpenAiErrorResponse.Create("Request rate limit exceeded.", type: "rate_limit_error", code: "RATE_LIMIT_EXCEEDED");
        await context.Response.WriteAsJsonAsync(envelope);
    }

    /// <summary>
    /// Choose the partition the limiter buckets the request into.  Auto prefers the
    /// ALB-forwarded mTLS subject when present so a misbehaving service is rate-limited
    /// individually instead of being lumped together by source IP behind the ALB.
    /// </summary>
    private string SelectPartitionKey(HttpContext context)
    {
        switch (_limitOptions.PartitionBy)
        {
            case PreAuthRateLimitPartition.MtlsSubject:
                return TryGetMtlsSubject(context, out var subject) ? subject : "anonymous-no-cert";

            case PreAuthRateLimitPartition.Ip:
                return ResolveRemoteIp(context);

            case PreAuthRateLimitPartition.Auto:
            default:
                return TryGetMtlsSubject(context, out var autoSubject) ? autoSubject : ResolveRemoteIp(context);
        }
    }

    private static bool TryGetMtlsSubject(HttpContext context, out string subject)
    {
        if (context.Request.Headers.TryGetValue(IdentityBrokerMiddleware.AlbMtlsSubjectHeader, out var header) &&
            header.Count == 1 &&
            !string.IsNullOrWhiteSpace(header[0]))
        {
            subject = header[0]!;
            return true;
        }
        subject = string.Empty;
        return false;
    }

    private static string ResolveRemoteIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

    public async ValueTask DisposeAsync()
    {
        await _limiter.DisposeAsync();
    }
}

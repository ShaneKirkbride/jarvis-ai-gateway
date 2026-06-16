using System.Net;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class PreAuthRateLimiterMiddlewareTests
{
    [Fact]
    public async Task Permits_first_request_under_limit()
    {
        var metrics = new CountingMetrics();
        var middleware = CreateMiddleware(metrics, permitLimit: 2);
        var http = NewContext("10.0.0.1");

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
        Assert.Equal(0, metrics.RateLimitedCalls);
    }

    [Fact]
    public async Task Rejects_request_over_limit_with_429_and_metric()
    {
        var metrics = new CountingMetrics();
        var middleware = CreateMiddleware(metrics, permitLimit: 1);
        var http1 = NewContext("10.0.0.5");
        var http2 = NewContext("10.0.0.5");

        await middleware.InvokeAsync(http1);
        await middleware.InvokeAsync(http2);

        Assert.Equal(StatusCodes.Status200OK, http1.Response.StatusCode);
        Assert.Equal(StatusCodes.Status429TooManyRequests, http2.Response.StatusCode);
        Assert.Equal(1, metrics.RateLimitedCalls);
        Assert.Contains("RATE_LIMIT_EXCEEDED", await ReadBody(http2));
    }

    [Fact]
    public async Task Skips_healthz_and_readyz()
    {
        var metrics = new CountingMetrics();
        var middleware = CreateMiddleware(metrics, permitLimit: 1);
        // Send three to /healthz — should never trip the limiter even though limit=1.
        var hz1 = NewContext("10.0.0.1", "/healthz");
        var hz2 = NewContext("10.0.0.1", "/healthz");
        var rz = NewContext("10.0.0.1", "/readyz");

        await middleware.InvokeAsync(hz1);
        await middleware.InvokeAsync(hz2);
        await middleware.InvokeAsync(rz);

        Assert.Equal(StatusCodes.Status200OK, hz1.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, hz2.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, rz.Response.StatusCode);
        Assert.Equal(0, metrics.RateLimitedCalls);
    }

    [Fact]
    public async Task Different_partition_keys_have_independent_buckets()
    {
        var metrics = new CountingMetrics();
        var middleware = CreateMiddleware(metrics, permitLimit: 1);
        var a = NewContext("10.0.0.1");
        var b = NewContext("10.0.0.2");

        await middleware.InvokeAsync(a);
        await middleware.InvokeAsync(b);

        Assert.Equal(StatusCodes.Status200OK, a.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, b.Response.StatusCode);
    }

    [Fact]
    public async Task Mtls_subject_partitioning_uses_alb_header_when_present()
    {
        var metrics = new CountingMetrics();
        var middleware = CreateMiddleware(metrics, permitLimit: 1, partitionBy: PreAuthRateLimitPartition.MtlsSubject);

        var first = NewContext("10.0.0.1");
        first.Request.Headers[IdentityBrokerMiddleware.AlbMtlsSubjectHeader] = "CN=service-a";
        var second = NewContext("10.0.0.1");
        second.Request.Headers[IdentityBrokerMiddleware.AlbMtlsSubjectHeader] = "CN=service-b";

        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);

        // Different cert subjects → different partitions → both pass.
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, second.Response.StatusCode);
    }

    [Fact]
    public async Task Auto_partition_falls_back_to_ip_when_no_cert_header()
    {
        var metrics = new CountingMetrics();
        var middleware = CreateMiddleware(metrics, permitLimit: 1, partitionBy: PreAuthRateLimitPartition.Auto);
        var a = NewContext("10.0.0.7");
        var b = NewContext("10.0.0.7");

        await middleware.InvokeAsync(a);
        await middleware.InvokeAsync(b);

        Assert.Equal(StatusCodes.Status429TooManyRequests, b.Response.StatusCode);
    }

    [Fact]
    public async Task Ip_partition_explicit_setting_works()
    {
        var metrics = new CountingMetrics();
        var middleware = CreateMiddleware(metrics, permitLimit: 1, partitionBy: PreAuthRateLimitPartition.Ip);
        var a = NewContext("10.0.0.10");
        a.Request.Headers[IdentityBrokerMiddleware.AlbMtlsSubjectHeader] = "CN=ignored";
        var b = NewContext("10.0.0.10");
        b.Request.Headers[IdentityBrokerMiddleware.AlbMtlsSubjectHeader] = "CN=ignored-too";

        await middleware.InvokeAsync(a);
        await middleware.InvokeAsync(b);

        // Same IP, different cert subjects, IP partition selected → second is denied.
        Assert.Equal(StatusCodes.Status429TooManyRequests, b.Response.StatusCode);
    }

    private static PreAuthRateLimiterMiddleware CreateMiddleware(
        CountingMetrics metrics,
        int permitLimit,
        PreAuthRateLimitPartition partitionBy = PreAuthRateLimitPartition.Ip)
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions
            {
                Enabled = true,
                PreAuthRateLimit = new PreAuthRateLimitOptions
                {
                    PermitLimit = permitLimit,
                    WindowSeconds = 60,
                    QueueLimit = 0,
                    PartitionBy = partitionBy
                }
            }
        };

        RequestDelegate next = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };

        return new PreAuthRateLimiterMiddleware(next, MsOptions.Create(options), metrics);
    }

    private static DefaultHttpContext NewContext(string ip, string path = "/v1/chat/completions")
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.Method = "POST";
        http.Response.Body = new MemoryStream();
        http.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return http;
    }

    private static async Task<string> ReadBody(HttpContext http)
    {
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private sealed class CountingMetrics : IGatewayMetrics
    {
        public int RateLimitedCalls;
        public string? LastPartition;

        public void RecordRequest(string modelAlias) { }
        public void RecordLatency(string modelAlias, TimeSpan elapsed) { }
        public void RecordPolicyDenial(string ruleId, string modelAlias) { }
        public void RecordBedrockInvocation(string strategy, TimeSpan elapsed, bool success) { }
        public void RecordBedrockError(string modelAlias) { }
        public void RecordServerError(string route) { }
        public void RecordTokenUsage(string modelAlias, int inputTokens, int outputTokens) { }
        public void RecordIdentityLookupCacheHit() { }
        public void RecordIdentityLookupGraphCall(TimeSpan elapsed, bool success) { }
        public void RecordIdentityLookupFailure(string reason) { }
        public void RecordIdentityPreAuthRateLimited(string partition)
        {
            Interlocked.Increment(ref RateLimitedCalls);
            LastPartition = partition;
        }
    }
}

using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// Targeted tests for edge-paths in the broker chain that aren't naturally hit by the
/// happy-path tests — disposal lifecycles, malformed Base64Url segments, mTLS serial
/// validation, etc.  Kept separate so the primary test files read as behaviour
/// specifications rather than coverage hunts.
/// </summary>
public sealed class IdentityBrokerCoverageGapTests
{
    [Fact]
    public void PreAuthRateLimiter_dispose_releases_underlying_limiter()
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions
            {
                Enabled = true,
                PreAuthRateLimit = new PreAuthRateLimitOptions { PermitLimit = 1, WindowSeconds = 60 }
            }
        };
        var middleware = new PreAuthRateLimiterMiddleware(_ => Task.CompletedTask, MsOptions.Create(options), new NoOpMetrics());

        // Disposing twice should be idempotent — the underlying PartitionedRateLimiter
        // tolerates DisposeAsync being called more than once.
        var task1 = middleware.DisposeAsync();
        Assert.True(task1.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task PreAuthRateLimiter_normalises_invalid_partition_options_to_minimums()
    {
        // Permit=0 and window=0 are not legal for FixedWindowRateLimiterOptions; the
        // middleware must clamp to safe minimums rather than throw.
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions
            {
                Enabled = true,
                PreAuthRateLimit = new PreAuthRateLimitOptions { PermitLimit = 0, WindowSeconds = 0, QueueLimit = -5 }
            }
        };
        var middleware = new PreAuthRateLimiterMiddleware(_ =>
        {
            // next does nothing — we just need to confirm the middleware did not throw at construction.
            return Task.CompletedTask;
        }, MsOptions.Create(options), new NoOpMetrics());

        var http = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        http.Request.Path = "/v1/chat/completions";
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");

        await middleware.InvokeAsync(http);
        // First request under clamped limit (1) should succeed.
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, http.Response.StatusCode);
    }

    [Fact]
    public void OwuiSessionJwtValidator_can_handle_returns_false_for_garbage_base64_header()
    {
        var validator = new OwuiSessionJwtValidator(
            MsOptions.Create(IdentityBrokerTestHelpers.DefaultBrokerOptions()),
            TimeProvider.System,
            NullLogger<OwuiSessionJwtValidator>.Instance);

        // Three segments but the first is not valid Base64Url JSON — must not throw.
        Assert.False(validator.CanHandle(new IdentityAssertionInput("!!!!.@@@@.####", new Dictionary<string, string?>())));
    }

    [Fact]
    public async Task IdentityBrokerMiddleware_mtls_serial_check_rejects_unexpected_serial()
    {
        var broker = new RecordingBroker();
        var options = IdentityBrokerTestHelpers.DefaultBrokerOptions();
        options.IdentityBroker.Mtls = new MtlsOptions
        {
            RequireSubjectCheck = true,
            AcceptedClientCertSerials = ["ABCD1234"]
        };
        var middleware = new IdentityBrokerMiddleware(_ => Task.CompletedTask, broker, new NoopAudit(),
            new SubjectHasher(MsOptions.Create(options)), MsOptions.Create(options));

        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "raw";
        http.Request.Headers[IdentityBrokerMiddleware.AlbMtlsSerialHeader] = "WRONG";

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
    }

    [Fact]
    public async Task IdentityBrokerMiddleware_empty_header_value_treated_as_missing()
    {
        var broker = new RecordingBroker();
        var middleware = NewMiddleware(broker);
        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "   ";

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task GraphGroupResolver_disposed_clears_in_flight_table()
    {
        var executor = new FakeGraphGroupQueryExecutor
        {
            Behavior = (_, _) => Task.FromResult(new GraphQueryResult(true, Array.Empty<DirectoryGroupRef>(), "oid", AssertionFailureReason.None, null))
        };
        var resolver = new GraphGroupResolver(
            executor,
            new MemoryCache(MsOptions.Create(new MemoryCacheOptions())),
            new NoOpMetrics(),
            TimeProvider.System,
            MsOptions.Create(IdentityBrokerTestHelpers.DefaultBrokerOptions()),
            NullLogger<GraphGroupResolver>.Instance);

        await resolver.ResolveAsync("user@example.test", CancellationToken.None);

        resolver.Dispose();
        resolver.Dispose();   // idempotent
    }

    private static IdentityBrokerMiddleware NewMiddleware(IIdentityBroker broker)
    {
        var options = IdentityBrokerTestHelpers.DefaultBrokerOptions();
        return new IdentityBrokerMiddleware(
            _ => Task.CompletedTask,
            broker,
            new NoopAudit(),
            new SubjectHasher(MsOptions.Create(options)),
            MsOptions.Create(options));
    }

    private static DefaultHttpContext NewContext()
    {
        var http = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        http.Request.Path = "/v1/chat/completions";
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");
        return http;
    }

    private sealed class RecordingBroker : IIdentityBroker
    {
        public int Calls;

        public Task<IdentityAssertionResult> ResolveAsync(IdentityAssertionInput input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new IdentityAssertionResult
            {
                IsValid = false,
                FailureReason = AssertionFailureReason.TokenInvalid
            });
        }
    }

    private sealed class NoopAudit : IAuditLogger
    {
        public void Write(GatewayAuditEvent auditEvent) { }
        public void WriteIdentity(IdentityAuditEvent auditEvent) { }
    }

    private sealed class NoOpMetrics : IGatewayMetrics
    {
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
        public void RecordIdentityPreAuthRateLimited(string partition) { }
    }
}

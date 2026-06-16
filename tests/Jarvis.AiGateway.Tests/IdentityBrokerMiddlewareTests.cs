using System.Net;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class IdentityBrokerMiddlewareTests
{
    [Fact]
    public async Task Healthz_skips_middleware()
    {
        var broker = new RecordingBroker();
        var middleware = CreateMiddleware(broker, out var audit);
        var http = NewContext(path: "/healthz");

        await middleware.InvokeAsync(http);

        Assert.Equal(0, broker.Calls);
        Assert.Empty(audit.Events);
        Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
    }

    [Fact]
    public async Task Readyz_skips_middleware()
    {
        var broker = new RecordingBroker();
        var middleware = CreateMiddleware(broker, out _);
        var http = NewContext(path: "/readyz");

        await middleware.InvokeAsync(http);

        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task Missing_header_returns_401_token_missing()
    {
        var broker = new RecordingBroker();
        var middleware = CreateMiddleware(broker, out var audit);
        var http = NewContext();

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Contains("IDENTITY_TOKEN_MISSING", await ReadBody(http));
        Assert.Equal("identity.token.missing", audit.Events[0].EventName);
    }

    [Fact]
    public async Task Multiple_header_values_rejected_as_token_invalid()
    {
        var broker = new RecordingBroker();
        var middleware = CreateMiddleware(broker, out _);
        var http = NewContext();
        http.Request.Headers.Append("X-Jarvis-User-Token", "first");
        http.Request.Headers.Append("X-Jarvis-User-Token", "second");

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Contains("IDENTITY_TOKEN_INVALID", await ReadBody(http));
    }

    [Fact]
    public async Task Bearer_prefix_stripped_before_broker_call()
    {
        var broker = new RecordingBroker(returnSuccess: true);
        var middleware = CreateMiddleware(broker, out _);
        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "Bearer raw-token-value";

        await middleware.InvokeAsync(http);

        Assert.Equal("raw-token-value", broker.LastAssertion);
    }

    [Fact]
    public async Task Token_invalid_response_uses_open_ai_envelope()
    {
        var broker = new RecordingBroker(failureReason: AssertionFailureReason.TokenInvalid);
        var middleware = CreateMiddleware(broker, out _);
        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "raw-token-value";

        await middleware.InvokeAsync(http);

        var body = await ReadBody(http);
        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Contains("\"code\":\"IDENTITY_TOKEN_INVALID\"", body);
        Assert.Contains("\"type\":\"invalid_request_error\"", body);
    }

    [Fact]
    public async Task Graph_lookup_failed_returns_503()
    {
        var broker = new RecordingBroker(failureReason: AssertionFailureReason.GraphLookupFailed);
        var middleware = CreateMiddleware(broker, out _);
        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "raw-token";

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
        Assert.Contains("IDENTITY_RESOLUTION_UNAVAILABLE", await ReadBody(http));
    }

    [Fact]
    public async Task Graph_user_not_found_returns_403()
    {
        var broker = new RecordingBroker(failureReason: AssertionFailureReason.GraphUserNotFound);
        var middleware = CreateMiddleware(broker, out _);
        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "raw-token";

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status403Forbidden, http.Response.StatusCode);
        Assert.Contains("IDENTITY_USER_NOT_FOUND", await ReadBody(http));
    }

    [Fact]
    public async Task Token_too_old_returns_401_with_too_old_code()
    {
        var broker = new RecordingBroker(failureReason: AssertionFailureReason.TokenTooOld);
        var middleware = CreateMiddleware(broker, out _);
        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "raw-token";

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Contains("IDENTITY_TOKEN_TOO_OLD", await ReadBody(http));
    }

    [Fact]
    public async Task Validator_not_found_returns_token_invalid_with_audit_event()
    {
        var broker = new RecordingBroker(failureReason: AssertionFailureReason.ValidatorNotFound);
        var middleware = CreateMiddleware(broker, out var audit);
        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "weird.token";

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Contains("IDENTITY_TOKEN_INVALID", await ReadBody(http));
        Assert.Equal("identity.validator.not_found", audit.Events[0].EventName);
    }

    [Fact]
    public async Task Successful_resolution_sets_user_and_calls_next_with_group_id_claims()
    {
        var broker = new RecordingBroker(returnSuccess: true);
        broker.SuccessResult = broker.SuccessResult with
        {
            EntraObjectId = "oid-from-graph",
            Groups = new HashSet<DirectoryGroupRef>
            {
                new("00000000-0000-0000-0000-000000000001", "Engineering"),
                new("00000000-0000-0000-0000-000000000002", "ITAR-Approved")
            }
        };
        var nextCalled = false;
        var middleware = CreateMiddleware(broker, out var audit, next: ctx =>
        {
            nextCalled = true;
            Assert.True(ctx.User.Identity?.IsAuthenticated == true);
            Assert.Equal(IdentityBrokerMiddleware.AuthenticationScheme, ctx.User.Identity?.AuthenticationType);
            Assert.Equal("user@example.test", ctx.User.FindFirst("sub")?.Value);
            Assert.Equal("oid-from-graph", ctx.User.FindFirst(IdentityBrokerMiddleware.EntraObjectIdClaim)?.Value);
            var ids = ctx.User.FindAll(IdentityBrokerMiddleware.GroupIdClaim).Select(c => c.Value).ToHashSet();
            Assert.Contains("00000000-0000-0000-0000-000000000001", ids);
            Assert.Contains("00000000-0000-0000-0000-000000000002", ids);
            return Task.CompletedTask;
        });

        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "valid-token";

        await middleware.InvokeAsync(http);

        Assert.True(nextCalled);
        Assert.Equal("identity.resolved", audit.Events[0].EventName);
        Assert.Equal(2, audit.Events[0].GroupCount);
    }

    [Fact]
    public async Task Mtls_subject_check_rejects_unexpected_subject()
    {
        var broker = new RecordingBroker();
        var options = IdentityBrokerTestHelpers.DefaultBrokerOptions();
        options.IdentityBroker.Mtls = new MtlsOptions
        {
            RequireSubjectCheck = true,
            AcceptedClientCertSubjects = ["CN=jarvis-owui"]
        };
        var middleware = CreateMiddleware(broker, out var audit, options: options);
        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "raw";
        http.Request.Headers[IdentityBrokerMiddleware.AlbMtlsSubjectHeader] = "CN=attacker";

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.Equal("mtls.subject_unexpected", audit.Events[0].EventName);
        Assert.Equal(0, broker.Calls);
    }

    [Fact]
    public async Task Mtls_subject_check_accepts_matching_subject()
    {
        var broker = new RecordingBroker(returnSuccess: true);
        var options = IdentityBrokerTestHelpers.DefaultBrokerOptions();
        options.IdentityBroker.Mtls = new MtlsOptions
        {
            RequireSubjectCheck = true,
            AcceptedClientCertSubjects = ["CN=jarvis-owui"]
        };
        var middleware = CreateMiddleware(broker, out _, options: options);
        var http = NewContext();
        http.Request.Headers["X-Jarvis-User-Token"] = "raw";
        http.Request.Headers[IdentityBrokerMiddleware.AlbMtlsSubjectHeader] = "CN=jarvis-owui";

        await middleware.InvokeAsync(http);

        Assert.Equal(1, broker.Calls);
    }

    [Fact]
    public async Task Models_listing_skipped_when_users_not_required()
    {
        var broker = new RecordingBroker();
        var options = IdentityBrokerTestHelpers.DefaultBrokerOptions();
        options.IdentityBroker.ModelsEndpointRequiresUser = false;
        var middleware = CreateMiddleware(broker, out _, options: options);
        var http = NewContext(path: "/v1/models");

        await middleware.InvokeAsync(http);

        Assert.Equal(0, broker.Calls);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
    }

    [Fact]
    public async Task Models_listing_requires_identity_by_default()
    {
        var broker = new RecordingBroker();
        var middleware = CreateMiddleware(broker, out _);
        var http = NewContext(path: "/v1/models");

        await middleware.InvokeAsync(http);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────

    private static IdentityBrokerMiddleware CreateMiddleware(
        RecordingBroker broker,
        out InMemoryIdentityAuditLogger audit,
        RequestDelegate? next = null,
        GatewayOptions? options = null)
    {
        audit = new InMemoryIdentityAuditLogger();
        next ??= _ => Task.CompletedTask;
        var opts = options ?? IdentityBrokerTestHelpers.DefaultBrokerOptions();
        var hasher = new SubjectHasher(MsOptions.Create(opts));
        return new IdentityBrokerMiddleware(next, broker, audit, hasher, MsOptions.Create(opts));
    }

    private static DefaultHttpContext NewContext(string path = "/v1/chat/completions")
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.Method = "POST";
        http.Response.Body = new MemoryStream();
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        return http;
    }

    private static async Task<string> ReadBody(HttpContext http)
    {
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private sealed class RecordingBroker : IIdentityBroker
    {
        public int Calls;
        public string? LastAssertion;
        private readonly AssertionFailureReason _failure;
        private readonly bool _success;
        public IdentityAssertionResult SuccessResult { get; set; }

        public RecordingBroker(bool returnSuccess = false, AssertionFailureReason failureReason = AssertionFailureReason.TokenInvalid)
        {
            _success = returnSuccess;
            _failure = failureReason;
            SuccessResult = new IdentityAssertionResult
            {
                IsValid = true,
                CanonicalSubject = "user@example.test",
                Email = "user@example.test",
                AssertionKind = "OwuiSessionJwt",
                IdentitySource = IdentitySource.ValidatorGraphFresh,
                Groups = new HashSet<DirectoryGroupRef>()
            };
        }

        public Task<IdentityAssertionResult> ResolveAsync(IdentityAssertionInput input, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            LastAssertion = input.RawAssertion;

            if (_success) return Task.FromResult(SuccessResult);
            return Task.FromResult(new IdentityAssertionResult
            {
                IsValid = false,
                FailureReason = _failure
            });
        }
    }

    private sealed class InMemoryIdentityAuditLogger : IAuditLogger
    {
        public List<IdentityAuditEvent> Events { get; } = [];
        public void Write(GatewayAuditEvent auditEvent) { }
        public void WriteIdentity(IdentityAuditEvent auditEvent) => Events.Add(auditEvent);
    }
}

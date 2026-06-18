using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// Unit-level coverage of the streaming orchestration path and <see cref="OpenAiSseStreamResult"/>.
/// </summary>
public sealed class StreamingChatCompletionTests
{
    [Fact]
    public async Task Stream_emits_role_then_content_then_done_and_records_usage()
    {
        var audit = new CapturingAuditLogger();
        var strategy = new ScriptedStreamingStrategy(
        [
            new AiChatTextDeltaEvent("Hello"),
            new AiChatTextDeltaEvent(", world"),
            new AiChatCompletionEvent("end_turn", new TokenUsage(7, 4, 11))
        ]);

        var (body, httpContext) = await RunStreamAsync(strategy, audit);
        var chunks = ParseChunks(body);

        Assert.EndsWith("data: [DONE]\n\n", body);
        Assert.Equal("text/event-stream", httpContext.Response.ContentType);
        Assert.Equal("no-cache", httpContext.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("keep-alive", httpContext.Response.Headers["Connection"].ToString());
        Assert.Equal("no", httpContext.Response.Headers["X-Accel-Buffering"].ToString());

        // First chunk advertises the assistant role.
        Assert.Equal("assistant", chunks[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());
        Assert.Equal("chat.completion.chunk", chunks[0].GetProperty("object").GetString());
        Assert.Equal("general", chunks[0].GetProperty("model").GetString());

        // Content chunks carry the text deltas verbatim.
        Assert.Equal("Hello", chunks[1].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
        Assert.Equal(", world", chunks[2].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());

        // Final chunk carries the mapped finish_reason and an empty delta.
        var final = chunks[^1];
        Assert.Equal("stop", final.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.False(final.GetProperty("choices")[0].GetProperty("delta").TryGetProperty("content", out _));

        var ev = Assert.Single(audit.Events);
        Assert.Equal("ALLOW", ev.Decision);
        Assert.Equal(7, ev.InputTokens);
        Assert.Equal(4, ev.OutputTokens);
        Assert.Equal(11, ev.TotalTokens);
        Assert.Equal("fake-stream", ev.InvocationStrategy);
    }

    [Fact]
    public async Task Stream_without_usage_leaves_token_counts_null()
    {
        var audit = new CapturingAuditLogger();
        var strategy = new ScriptedStreamingStrategy(
        [
            new AiChatTextDeltaEvent("hi"),
            new AiChatCompletionEvent("end_turn", null)
        ]);

        var (body, _) = await RunStreamAsync(strategy, audit);

        Assert.Contains("data: [DONE]", body);
        var ev = Assert.Single(audit.Events);
        Assert.Equal("ALLOW", ev.Decision);
        Assert.Null(ev.InputTokens);
        Assert.Null(ev.OutputTokens);
        Assert.Null(ev.TotalTokens);
    }

    [Fact]
    public async Task Stream_redacts_secrets_in_deltas_when_redaction_enabled()
    {
        var audit = new CapturingAuditLogger();
        var strategy = new ScriptedStreamingStrategy(
        [
            new AiChatTextDeltaEvent("key is AKIAIOSFODNN7EXAMPLE done"),
            new AiChatCompletionEvent("end_turn", new TokenUsage(1, 1, 2))
        ]);

        var (body, _) = await RunStreamAsync(strategy, audit);

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", body);
        Assert.Contains("[REDACTED_AWS_ACCESS_KEY]", body);
        Assert.True(audit.Events.Single().RedactionCount >= 1);
    }

    [Fact]
    public async Task Stream_skips_empty_text_deltas()
    {
        var audit = new CapturingAuditLogger();
        var strategy = new ScriptedStreamingStrategy(
        [
            new AiChatTextDeltaEvent(string.Empty),
            new AiChatTextDeltaEvent("real"),
            new AiChatCompletionEvent("end_turn", null)
        ]);

        var (body, _) = await RunStreamAsync(strategy, audit);
        var chunks = ParseChunks(body);

        // role chunk + one content chunk + final chunk = 3 (empty delta produced no chunk).
        Assert.Equal(3, chunks.Count);
        Assert.Equal("real", chunks[1].GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("content_filtered", "content_filter")]
    [InlineData("guardrail_intervened", "content_filter")]
    [InlineData("tool_use", "tool_calls")]
    [InlineData("something_unexpected", "stop")]
    public async Task Finish_reason_is_mapped_to_openai_value(string providerReason, string expected)
    {
        var strategy = new ScriptedStreamingStrategy(
        [
            new AiChatTextDeltaEvent("x"),
            new AiChatCompletionEvent(providerReason, null)
        ]);

        var (body, _) = await RunStreamAsync(strategy, new CapturingAuditLogger());
        var final = ParseChunks(body)[^1];

        Assert.Equal(expected, final.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task Provider_error_before_first_byte_maps_to_error_envelope()
    {
        var audit = new CapturingAuditLogger();
        var strategy = new ScriptedStreamingStrategy([], throwAtStart: new Amazon.BedrockRuntime.Model.AccessDeniedException("denied"));

        var (body, httpContext) = await RunStreamAsync(strategy, audit);

        Assert.Equal(StatusCodes.Status502BadGateway, httpContext.Response.StatusCode);
        Assert.Contains("provider_access_denied", body);
        Assert.DoesNotContain("chat.completion.chunk", body);
        var ev = Assert.Single(audit.Events);
        Assert.Equal("ERROR", ev.Decision);
        Assert.Equal("provider_access_denied", ev.ErrorType);
    }

    [Fact]
    public async Task Cancellation_before_first_byte_records_cancelled_audit_without_body()
    {
        var audit = new CapturingAuditLogger();
        using var cts = new CancellationTokenSource();
        var strategy = new ScriptedStreamingStrategy([], throwAtStart: new OperationCanceledException(), beforeThrow: cts.Cancel);

        var (body, _) = await RunStreamAsync(strategy, audit, cts.Token);

        Assert.Equal(string.Empty, body);
        var ev = Assert.Single(audit.Events);
        Assert.Equal("CANCELLED", ev.Decision);
        Assert.Equal("cancellation", ev.ErrorCategory);
    }

    [Fact]
    public async Task Cancellation_mid_stream_records_cancelled_audit()
    {
        var audit = new CapturingAuditLogger();
        using var cts = new CancellationTokenSource();
        var strategy = new ScriptedStreamingStrategy(
            [new AiChatTextDeltaEvent("partial")],
            throwAfterEvents: new OperationCanceledException(),
            beforeThrow: cts.Cancel);

        var (body, _) = await RunStreamAsync(strategy, audit, cts.Token);

        // The role chunk and first content chunk were already written before cancellation.
        Assert.Contains("partial", body);
        Assert.DoesNotContain("data: [DONE]", body);
        var ev = Assert.Single(audit.Events);
        Assert.Equal("CANCELLED", ev.Decision);
    }

    [Fact]
    public async Task Provider_error_mid_stream_emits_trailing_error_event_then_done()
    {
        var audit = new CapturingAuditLogger();
        var strategy = new ScriptedStreamingStrategy(
            [new AiChatTextDeltaEvent("partial")],
            throwAfterEvents: new Amazon.BedrockRuntime.Model.ThrottlingException("slow down"));

        var (body, httpContext) = await RunStreamAsync(strategy, audit);

        // Status is already 200 (committed to the stream); the error is surfaced inside the stream.
        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        Assert.Contains("partial", body);
        Assert.Contains("provider_throttled", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
        var ev = Assert.Single(audit.Events);
        Assert.Equal("ERROR", ev.Decision);
        Assert.Equal("provider_throttled", ev.ErrorType);
    }

    [Fact]
    public async Task Policy_denial_returns_403_before_stream_starts()
    {
        var audit = new CapturingAuditLogger();
        var options = StreamingOptionsConfig();
        var strategy = new ScriptedStreamingStrategy([new AiChatCompletionEvent("end_turn", null)]);
        var orchestrator = BuildOrchestrator(options, audit, new DenyPolicyEngine(), streamingStrategy: strategy);

        var (result, httpContext) = await CompleteAsync(orchestrator, stream: true);
        var body = await ExecuteResult(result, httpContext);

        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
        Assert.Contains("USER_GROUP_DENIED", body);
        Assert.False(strategy.WasInvoked);
        Assert.Equal("DENY", audit.Events.Single().Decision);
    }

    [Fact]
    public async Task Stream_without_capable_strategy_returns_400_when_fallback_disabled()
    {
        var audit = new CapturingAuditLogger();
        var options = StreamingOptionsConfig(fallback: false);
        var strategy = new ScriptedStreamingStrategy([]) { CanHandleResult = false };
        var orchestrator = BuildOrchestrator(options, audit, new AllowPolicyEngine(Model()), streamingStrategy: strategy);

        var (result, httpContext) = await CompleteAsync(orchestrator, stream: true);
        var body = await ExecuteResult(result, httpContext);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Contains("streaming_not_supported", body);
    }

    [Fact]
    public async Task Stream_falls_back_to_non_streaming_when_no_capable_strategy_and_fallback_enabled()
    {
        var audit = new CapturingAuditLogger();
        var options = StreamingOptionsConfig(fallback: true);
        var orchestrator = BuildOrchestrator(
            options,
            audit,
            new AllowPolicyEngine(Model()),
            streamingStrategy: new ScriptedStreamingStrategy([]) { CanHandleResult = false },
            nonStreamingStrategy: new FakeNonStreamingStrategy());

        var (result, httpContext) = await CompleteAsync(orchestrator, stream: true);
        var body = await ExecuteResult(result, httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
    }

    [Fact]
    public async Task Write_failure_mid_stream_is_handled_without_throwing()
    {
        // Simulates the client connection dropping: every write to the response body throws.
        // The result must record an ERROR audit and swallow the failed trailing-error write
        // rather than letting the exception escape the endpoint.
        var audit = new CapturingAuditLogger();
        var strategy = new ScriptedStreamingStrategy(
        [
            new AiChatTextDeltaEvent("hi"),
            new AiChatCompletionEvent("end_turn", null)
        ]);
        var orchestrator = BuildOrchestrator(StreamingOptionsConfig(), audit, new AllowPolicyEngine(Model()), streamingStrategy: strategy);
        var (result, httpContext) = await CompleteAsync(orchestrator, stream: true);
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        httpContext.Response.Body = new ThrowingStream();

        await result.ExecuteAsync(httpContext);

        Assert.Equal("ERROR", audit.Events.Single().Decision);
    }

    [Fact]
    public async Task Enumerator_dispose_failure_does_not_break_a_completed_stream()
    {
        var audit = new CapturingAuditLogger();
        var orchestrator = BuildOrchestrator(StreamingOptionsConfig(), audit, new AllowPolicyEngine(Model()), streamingStrategy: new DisposeThrowingStreamingStrategy());
        var (result, httpContext) = await CompleteAsync(orchestrator, stream: true);

        var body = await ExecuteResult(result, httpContext);

        Assert.EndsWith("data: [DONE]\n\n", body);
        Assert.Equal("ALLOW", audit.Events.Single().Decision);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static GatewayOptions StreamingOptionsConfig(bool fallback = false) =>
        new() { Streaming = new StreamingOptions { FallbackToNonStreaming = fallback } };

    private async Task<(string Body, HttpContext Context)> RunStreamAsync(
        ScriptedStreamingStrategy strategy,
        CapturingAuditLogger audit,
        CancellationToken requestAborted = default)
    {
        var orchestrator = BuildOrchestrator(StreamingOptionsConfig(), audit, new AllowPolicyEngine(Model()), streamingStrategy: strategy);
        var (result, httpContext) = await CompleteAsync(orchestrator, stream: true);
        httpContext.RequestAborted = requestAborted;
        var body = await ExecuteResult(result, httpContext);
        return (body, httpContext);
    }

    private static ChatCompletionOrchestrator BuildOrchestrator(
        GatewayOptions options,
        IAuditLogger audit,
        IPolicyEngine policyEngine,
        IBedrockStreamingStrategy? streamingStrategy = null,
        IBedrockInvocationStrategy? nonStreamingStrategy = null) =>
        new(
            new UserContextFactory(MsOptions.Create(options)),
            new RequestContextFactory(),
            new OpenAiChatRequestValidator(MsOptions.Create(options)),
            policyEngine,
            nonStreamingStrategy is null ? [] : [nonStreamingStrategy],
            new RegexContentRedactor(MsOptions.Create(options)),
            audit,
            new OpenAiErrorMapper(),
            MsOptions.Create(options),
            streamingStrategy is null ? [] : [streamingStrategy]);

    private static async Task<(IResult Result, HttpContext Context)> CompleteAsync(ChatCompletionOrchestrator orchestrator, bool stream)
    {
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace-id" };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user")], "test"));
        var request = new OpenAiChatCompletionRequest
        {
            Model = "general",
            Stream = stream,
            Messages = [new OpenAiMessage { Role = "user", Content = JsonSerializer.SerializeToElement("hello") }]
        };

        var result = await orchestrator.CompleteAsync(httpContext, request, CancellationToken.None);
        return (result, httpContext);
    }

    private static async Task<string> ExecuteResult(IResult result, HttpContext context)
    {
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static List<JsonElement> ParseChunks(string body)
    {
        var docs = new List<JsonElement>();
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var payload = line["data: ".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;
            docs.Add(JsonDocument.Parse(payload).RootElement.Clone());
        }

        return docs;
    }

    private static GatewayModel Model() => new()
    {
        Id = "general",
        Alias = "general",
        BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0",
        ProviderName = "fake",
        SupportsConverse = true,
        OutputModalities = ["TEXT"],
        MaxOutputTokens = 100
    };

    private sealed class ScriptedStreamingStrategy(
        IReadOnlyList<AiChatStreamEvent> events,
        Exception? throwAtStart = null,
        Exception? throwAfterEvents = null,
        Action? beforeThrow = null) : IBedrockStreamingStrategy
    {
        public bool CanHandleResult { get; init; } = true;
        public bool WasInvoked { get; private set; }
        public string Name => "fake-stream";

        public bool CanHandle(GatewayModel model, AiChatRequest request) => CanHandleResult;

        public async IAsyncEnumerable<AiChatStreamEvent> StreamAsync(
            GatewayModel model,
            AiChatRequest request,
            RequestContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            WasInvoked = true;
            if (throwAtStart is not null)
            {
                beforeThrow?.Invoke();
                await Task.Yield();
                throw throwAtStart;
            }

            foreach (var streamEvent in events)
            {
                yield return streamEvent;
            }

            if (throwAfterEvents is not null)
            {
                beforeThrow?.Invoke();
                await Task.Yield();
                throw throwAfterEvents;
            }
        }
    }

    private sealed class FakeNonStreamingStrategy : IBedrockInvocationStrategy
    {
        public string Name => "fake-nonstream";
        public bool CanHandle(GatewayModel model, AiChatRequest request) => true;
        public Task<AiChatResult> InvokeAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiChatResult("fallback", new TokenUsage(1, 1, 2), "stop", new ProviderInvocationMetadata("fake", Name, 1)));
    }

    private sealed class DisposeThrowingStreamingStrategy : IBedrockStreamingStrategy
    {
        public string Name => "dispose-throw";
        public bool CanHandle(GatewayModel model, AiChatRequest request) => true;
        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            new DisposeThrowingEnumerable();

        private sealed class DisposeThrowingEnumerable : IAsyncEnumerable<AiChatStreamEvent>
        {
            public IAsyncEnumerator<AiChatStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator();
        }

        private sealed class Enumerator : IAsyncEnumerator<AiChatStreamEvent>
        {
            private readonly AiChatStreamEvent[] _events = [new AiChatTextDeltaEvent("hi"), new AiChatCompletionEvent("end_turn", null)];
            private int _index = -1;
            public AiChatStreamEvent Current => _events[_index];
            public ValueTask<bool> MoveNextAsync()
            {
                _index++;
                return ValueTask.FromResult(_index < _events.Length);
            }

            public ValueTask DisposeAsync() => throw new InvalidOperationException("dispose failed");
        }
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override void Flush() => throw new IOException("write failed");
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new IOException("write failed");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("write failed");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("write failed");
    }

    private sealed class AllowPolicyEngine(GatewayModel model) : IPolicyEngine
    {
        public Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyDecision(true, "ALLOW", model) { RuleId = PolicyRuleIds.Allow });

        public Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GatewayModel>>([model]);
    }

    private sealed class DenyPolicyEngine : IPolicyEngine
    {
        public Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyDecision(false, "blocked", null, PolicyRuleIds.UserGroupDenied));

        public Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GatewayModel>>([]);
    }

    private sealed class CapturingAuditLogger : IAuditLogger
    {
        public List<GatewayAuditEvent> Events { get; } = [];
        public void Write(GatewayAuditEvent auditEvent) => Events.Add(auditEvent);
        public void WriteIdentity(IdentityAuditEvent auditEvent) { }
    }
}

/// <summary>
/// HTTP-level streaming contract coverage through the full middleware + endpoint pipeline.
/// </summary>
public sealed class StreamingChatCompletionHttpTests : IClassFixture<StreamingChatCompletionHttpTests.StreamingGatewayFactory>
{
    private readonly StreamingGatewayFactory _factory;

    public StreamingChatCompletionHttpTests(StreamingGatewayFactory factory) => _factory = factory;

    [Fact]
    public async Task Stream_true_returns_event_stream_with_chunks_and_done()
    {
        var client = AuthedClient();
        var response = await client.PostAsync("/v1/chat/completions", Json(new
        {
            model = "general",
            stream = true,
            messages = new[] { new { role = "user", content = "hello" } }
        }));

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"object\":\"chat.completion.chunk\"", body);
        Assert.Contains("\"role\":\"assistant\"", body);
        Assert.Contains("token-from-stream", body);
        Assert.EndsWith("data: [DONE]\n\n", body);
    }

    [Fact]
    public async Task Stream_false_still_returns_json_completion()
    {
        var client = AuthedClient();
        var response = await client.PostAsync("/v1/chat/completions", Json(new
        {
            model = "general",
            stream = false,
            messages = new[] { new { role = "user", content = "hello" } }
        }));

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
    }

    [Fact]
    public async Task Stream_true_policy_denial_returns_403_before_stream()
    {
        var client = AuthedClient();
        var response = await client.PostAsync("/v1/chat/completions", Json(new
        {
            model = "restricted",
            stream = true,
            messages = new[] { new { role = "user", content = "hello" } }
        }));

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("USER_GROUP_DENIED", body);
    }

    [Fact]
    public async Task Stream_true_provider_access_denied_maps_to_provider_error()
    {
        var client = AuthedClient();
        var response = await client.PostAsync("/v1/chat/completions", Json(new
        {
            model = "denied-stream",
            stream = true,
            messages = new[] { new { role = "user", content = "hello" } }
        }));

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("provider_access_denied", body);
    }

    private System.Net.Http.HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");
        return client;
    }

    private static StringContent Json<T>(T value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public sealed class StreamingGatewayFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Authority"] = "https://issuer.example.test/",
                    ["Jwt:Audience"] = "jarvis",
                    ["Jwt:RequireHttpsMetadata"] = "false",
                    ["Gateway:IdentityBroker:Enabled"] = "false",
                    ["Gateway:ModelDiscovery:Enabled"] = "false",
                    ["Gateway:Models:0:Alias"] = "general",
                    ["Gateway:Models:0:BedrockModelId"] = "anthropic.claude-3-haiku-20240307-v1:0",
                    ["Gateway:Models:0:RequiredGroups:0"] = "AI-General-Users",
                    ["Gateway:Models:0:SupportsConverse"] = "true",
                    ["Gateway:Models:1:Alias"] = "restricted",
                    ["Gateway:Models:1:BedrockModelId"] = "anthropic.claude-3-sonnet-20240229-v1:0",
                    ["Gateway:Models:1:RequiredGroups:0"] = "Restricted-Users",
                    ["Gateway:Models:1:SupportsConverse"] = "true",
                    ["Gateway:Models:2:Alias"] = "denied-stream",
                    ["Gateway:Models:2:BedrockModelId"] = "anthropic.claude-3-haiku-20240307-v1:0",
                    ["Gateway:Models:2:RequiredGroups:0"] = "AI-General-Users",
                    ["Gateway:Models:2:SupportsConverse"] = "true"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticationSchemeProvider>();
                services.AddSingleton<IAuthenticationSchemeProvider, StreamingSchemeProvider>();
                services.RemoveAll<IBedrockInvocationStrategy>();
                services.AddSingleton<IBedrockInvocationStrategy, FakeNonStreamingStrategy>();
                services.RemoveAll<IBedrockStreamingStrategy>();
                services.AddSingleton<IBedrockStreamingStrategy, FakeHttpStreamingStrategy>();
                services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
                services.RemoveAll<IGraphGroupQueryExecutor>();
                services.AddSingleton<IGraphGroupQueryExecutor, FakeGraphGroupQueryExecutor>();
            });
        }
    }

    private sealed class StreamingSchemeProvider : AuthenticationSchemeProvider
    {
        private static readonly AuthenticationScheme TestScheme = new("Bearer", "Bearer", typeof(StreamingAuthHandler));

        public StreamingSchemeProvider(IOptions<AuthenticationOptions> options) : base(options) { }

        public override Task<AuthenticationScheme?> GetSchemeAsync(string name) =>
            Task.FromResult<AuthenticationScheme?>(name == "Bearer" ? TestScheme : null);

        public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);

        public override Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);

        public override Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() =>
            Task.FromResult<IEnumerable<AuthenticationScheme>>([TestScheme]);
    }

    private sealed class StreamingAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim("sub", "stream-user"),
                new Claim("email", "stream-user@example.test"),
                new Claim("groups", "AI-General-Users")
            };
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class FakeHttpStreamingStrategy : IBedrockStreamingStrategy
    {
        public string Name => "fake-http-stream";
        public bool CanHandle(GatewayModel model, AiChatRequest request) => true;

        public async IAsyncEnumerable<AiChatStreamEvent> StreamAsync(
            GatewayModel model,
            AiChatRequest request,
            RequestContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (model.Id == "denied-stream")
            {
                await Task.Yield();
                throw new Amazon.BedrockRuntime.Model.AccessDeniedException("denied");
            }

            await Task.Yield();
            yield return new AiChatTextDeltaEvent("token-from-stream");
            yield return new AiChatCompletionEvent("end_turn", new TokenUsage(3, 2, 5));
        }
    }

    private sealed class FakeNonStreamingStrategy : IBedrockInvocationStrategy
    {
        public string Name => "fake-nonstream";
        public bool CanHandle(GatewayModel model, AiChatRequest request) => true;
        public Task<AiChatResult> InvokeAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiChatResult("non-streaming", new TokenUsage(1, 1, 2), "stop", new ProviderInvocationMetadata("fake", Name, 1)));
    }

    private sealed class NoOpAuditLogger : IAuditLogger
    {
        public void Write(GatewayAuditEvent auditEvent) { }
        public void WriteIdentity(IdentityAuditEvent auditEvent) { }
    }
}

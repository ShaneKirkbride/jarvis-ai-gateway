using System.Security.Claims;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

/// <summary>Unit coverage for the Anthropic <c>/v1/messages</c> orchestrator (Phase 4).</summary>
public sealed class MessagesOrchestratorTests
{
    [Fact]
    public async Task Valid_request_returns_anthropic_message()
    {
        var request = Req([User("Hello there")]);
        var (status, body) = await Run(Build(Allow(), new FakeChatProvider("Hi!")), request);

        Assert.Equal(StatusCodes.Status200OK, status);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.StartsWith("msg_", root.GetProperty("id").GetString());
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.Equal("text", root.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("Hi!", root.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
        Assert.Equal(3, root.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(2, root.GetProperty("usage").GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public async Task System_prompt_string_is_accepted()
    {
        var request = Req([User("Hello")], system: JsonSerializer.SerializeToElement("You are helpful."));
        var (status, _) = await Run(Build(Allow(), new FakeChatProvider("ok")), request);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task System_prompt_as_text_blocks_is_accepted()
    {
        var system = JsonSerializer.SerializeToElement(new[] { new { type = "text", text = "Be concise." } });
        var request = Req([User("Hello")], system: system);
        var (status, _) = await Run(Build(Allow(), new FakeChatProvider("ok")), request);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Content_blocks_array_of_text_is_accepted()
    {
        var content = JsonSerializer.SerializeToElement(new[] { new { type = "text", text = "part one" }, new { type = "text", text = "part two" } });
        var request = Req([new AnthropicMessage { Role = "user", Content = content }]);
        var (status, _) = await Run(Build(Allow(), new FakeChatProvider("ok")), request);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Multi_turn_conversation_is_accepted()
    {
        var request = Req([User("hi"), Assistant("hello"), User("how are you?")]);
        var (status, _) = await Run(Build(Allow(), new FakeChatProvider("good")), request);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Image_content_block_is_rejected()
    {
        var content = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "image", source = new { type = "base64", media_type = "image/png", data = "AAAA" } }
        });
        var request = Req([new AnthropicMessage { Role = "user", Content = content }]);
        var (status, body) = await Run(Build(Allow(), new FakeChatProvider("ok")), request);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("invalid_request_error", body);
        Assert.Contains("only text content", body);
    }

    [Fact]
    public async Task Unsupported_system_content_is_rejected()
    {
        var request = Req([User("hi")], system: JsonSerializer.SerializeToElement(123));
        var (status, body) = await Run(Build(Allow(), new FakeChatProvider("ok")), request);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("system", body);
    }

    [Fact]
    public async Task Streaming_is_rejected()
    {
        var request = Req([User("hi")], stream: true);
        var (status, body) = await Run(Build(Allow(), new FakeChatProvider("ok")), request);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("Streaming is not supported", body);
    }

    [Fact]
    public async Task Missing_max_tokens_is_rejected()
    {
        var request = Req([User("hi")], maxTokens: null);
        var (status, body) = await Run(Build(Allow(), new FakeChatProvider("ok")), request);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("max_tokens", body);
    }

    [Fact]
    public async Task Missing_model_is_rejected()
    {
        var request = Req([User("hi")], model: "");
        var (status, body) = await Run(Build(Allow(), new FakeChatProvider("ok")), request);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("model", body);
    }

    [Fact]
    public async Task Empty_conversation_is_rejected_by_validator()
    {
        var request = Req([]);
        var (status, body) = await Run(Build(Allow(), new FakeChatProvider("ok")), request);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("invalid_request_error", body);
    }

    [Fact]
    public async Task Policy_denial_returns_403_permission_error()
    {
        var request = Req([User("hi")]);
        var (status, body) = await Run(Build(new DenyPolicy(), new FakeChatProvider("ok")), request);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains("permission_error", body);
    }

    [Theory]
    [InlineData("length", "max_tokens")]
    [InlineData("max_tokens", "max_tokens")]
    [InlineData("tool_use", "tool_use")]
    [InlineData("tool_calls", "tool_use")]
    [InlineData("stop_sequence", "stop_sequence")]
    [InlineData("stop", "end_turn")]
    public async Task Stop_reason_is_mapped(string providerReason, string expected)
    {
        var request = Req([User("hi")]);
        var (status, body) = await Run(Build(Allow(), new FakeChatProvider("done", providerReason)), request);

        Assert.Equal(StatusCodes.Status200OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(expected, doc.RootElement.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public async Task Response_text_is_redacted_outbound()
    {
        var request = Req([User("hi")]);
        var (_, body) = await Run(Build(Allow(), new FakeChatProvider("key = AKIAIOSFODNN7EXAMPLE")), request);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", body);
    }

    [Fact]
    public async Task Provider_failure_returns_anthropic_error_envelope()
    {
        var request = Req([User("hi")]);
        var (status, body) = await Run(Build(Allow(), new ThrowingProvider()), request);

        Assert.Equal(StatusCodes.Status502BadGateway, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("api_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Provider_throttling_maps_to_rate_limit_error()
    {
        var request = Req([User("hi")]);
        var (status, body) = await Run(Build(Allow(), new ThrottlingProvider()), request);

        Assert.Equal(StatusCodes.Status429TooManyRequests, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("rate_limit_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Stop_sequences_are_forwarded()
    {
        var request = Req([User("hi")]);
        request.StopSequences = ["\n\n", "END"];
        var (status, _) = await Run(Build(Allow(), new FakeChatProvider("ok")), request);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static MessagesOrchestrator Build(IPolicyEngine policy, IAiProvider provider, GatewayOptions? options = null)
    {
        options ??= new GatewayOptions();
        return new MessagesOrchestrator(
            new UserContextFactory(MsOptions.Create(options)),
            new RequestContextFactory(),
            new OpenAiChatRequestValidator(MsOptions.Create(options)),
            policy,
            [provider],
            new RegexContentRedactor(MsOptions.Create(options)),
            new CapturingAuditLogger(),
            new OpenAiErrorMapper(),
            new NoOpMetrics(),
            MsOptions.Create(options),
            new ResiliencePipelineBuilder().Build());
    }

    private static AnthropicMessagesRequest Req(List<AnthropicMessage> messages, JsonElement? system = null, int? maxTokens = 256, bool stream = false, string model = "chat-model") =>
        new() { Model = model, Messages = messages, System = system, MaxTokens = maxTokens, Stream = stream };

    private static AnthropicMessage User(string text) => new() { Role = "user", Content = JsonSerializer.SerializeToElement(text) };
    private static AnthropicMessage Assistant(string text) => new() { Role = "assistant", Content = JsonSerializer.SerializeToElement(text) };

    private static async Task<(int Status, string Body)> Run(MessagesOrchestrator orchestrator, AnthropicMessagesRequest request)
    {
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace" };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user")], "test"));
        var result = await orchestrator.CreateMessageAsync(httpContext, request, CancellationToken.None);
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        return (httpContext.Response.StatusCode, await new StreamReader(httpContext.Response.Body).ReadToEndAsync());
    }

    private static GatewayModel Model() => new() { Id = "chat-model", Alias = "chat-model", ProviderName = "azure-openai", MaxInputCharacters = 100000, MaxOutputTokens = 4096 };

    private static AllowPolicy Allow() => new(Model());

    private sealed class FakeChatProvider(string text, string finishReason = "stop") : IAiProvider
    {
        public string ProviderName => "azure-openai";
        public Task<AiChatResult> CompleteAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiChatResult(text, new TokenUsage(3, 2, 5), finishReason, new ProviderInvocationMetadata("azure-openai", "azure-openai", 1)));
        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ThrowingProvider : IAiProvider
    {
        public string ProviderName => "azure-openai";
        public Task<AiChatResult> CompleteAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ThrottlingProvider : IAiProvider
    {
        public string ProviderName => "azure-openai";
        public Task<AiChatResult> CompleteAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new Amazon.BedrockRuntime.Model.ThrottlingException("throttled");
        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AllowPolicy(GatewayModel model) : IPolicyEngine
    {
        public Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyDecision(true, "ALLOW", model) { RuleId = PolicyRuleIds.Allow });
        public Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>([]);
    }

    private sealed class DenyPolicy : IPolicyEngine
    {
        public Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyDecision(false, "blocked", null, PolicyRuleIds.UserGroupDenied));
        public Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>([]);
    }

    private sealed class CapturingAuditLogger : IAuditLogger
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

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

public sealed class CompletionsOrchestratorTests
{
    [Fact]
    public async Task Valid_fim_request_returns_text_completion()
    {
        var (status, body) = await Run(Build(new AllowCompletionPolicy(Model()), new FakeCompletionProvider()), Json("def add("), suffix: "    return a + b");

        Assert.Equal(StatusCodes.Status200OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("text_completion", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal("a, b):", doc.RootElement.GetProperty("choices")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Autocomplete_is_disabled_for_itar_requests()
    {
        var (status, body) = await Run(Build(new AllowCompletionPolicy(Model()), new FakeCompletionProvider()), Json("x"), itar: true);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains("AUTOCOMPLETE_DISABLED_FOR_ITAR", body);
    }

    [Fact]
    public async Task Itar_disable_can_be_turned_off_by_config()
    {
        var options = new GatewayOptions { Completions = new CompletionsOptions { DisableForItar = false } };
        var (status, _) = await Run(Build(new AllowCompletionPolicy(Model()), new FakeCompletionProvider(), options), Json("x"), itar: true, options: options);

        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task Oversized_context_is_rejected()
    {
        var options = new GatewayOptions { Completions = new CompletionsOptions { MaxContextCharacters = 5 } };
        var (status, body) = await Run(Build(new AllowCompletionPolicy(Model()), new FakeCompletionProvider(), options), Json("this is way too long"), options: options);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("context_too_large", body);
    }

    [Theory]
    [InlineData("\"\"", "prompt_required")]
    [InlineData("[\"a\",\"b\"]", "unsupported_prompt")]   // batched prompts not supported
    [InlineData("123", "unsupported_prompt")]
    public async Task Invalid_prompt_is_rejected(string promptJson, string expectedCode)
    {
        var (status, body) = await Run(Build(new AllowCompletionPolicy(Model()), new FakeCompletionProvider()), JsonDocument.Parse(promptJson).RootElement.Clone());

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains(expectedCode, body);
    }

    [Fact]
    public async Task Missing_model_is_rejected()
    {
        var (status, body) = await Run(Build(new AllowCompletionPolicy(Model()), new FakeCompletionProvider()), Json("x"), model: "");
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("model_required", body);
    }

    [Fact]
    public async Task Policy_denial_returns_403()
    {
        var (status, body) = await Run(Build(new DenyCompletionPolicy(), new FakeCompletionProvider()), Json("x"));
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains("USER_GROUP_DENIED", body);
    }

    [Fact]
    public async Task Provider_without_completion_capability_is_not_supported()
    {
        var (status, body) = await Run(Build(new AllowCompletionPolicy(Model()), new ChatOnlyProvider()), Json("x"));
        Assert.Equal(StatusCodes.Status501NotImplemented, status);
        Assert.Contains("unsupported_model", body);
    }

    [Fact]
    public async Task Completion_text_is_redacted_outbound()
    {
        var (_, body) = await Run(Build(new AllowCompletionPolicy(Model()), new SecretLeakingProvider()), Json("x"));
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", body);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static CompletionsOrchestrator Build(IPolicyEngine policy, IAiProvider provider, GatewayOptions? options = null)
    {
        options ??= new GatewayOptions();
        return new CompletionsOrchestrator(
            new UserContextFactory(MsOptions.Create(options)),
            new RequestContextFactory(),
            policy,
            [provider],
            new RegexContentRedactor(MsOptions.Create(options)),
            new CapturingAuditLogger(),
            new OpenAiErrorMapper(),
            new NoOpMetrics(),
            MsOptions.Create(options),
            new ResiliencePipelineBuilder().Build());
    }

    private static async Task<(int Status, string Body)> Run(CompletionsOrchestrator orchestrator, JsonElement prompt, string? suffix = null, bool itar = false, string model = "fim-model", GatewayOptions? options = null)
    {
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace" };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user")], "test"));
        if (itar) httpContext.Request.Headers["X-Jarvis-Itar-Mode"] = "true";
        var request = new OpenAiCompletionRequest { Model = model, Prompt = prompt, Suffix = suffix };

        var result = await orchestrator.CompleteAsync(httpContext, request, CancellationToken.None);
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        return (httpContext.Response.StatusCode, await new StreamReader(httpContext.Response.Body).ReadToEndAsync());
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static GatewayModel Model() => new() { Id = "fim-model", Alias = "fim-model", ProviderName = "azure-openai", SupportsFim = true, MaxInputCharacters = 100000, MaxOutputTokens = 256 };

    private sealed class FakeCompletionProvider : IAiProvider, ICompletionProvider
    {
        public string ProviderName => "azure-openai";
        public Task<AiChatResult> CompleteAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiCompletionResult> CompleteTextAsync(GatewayModel model, AiCompletionRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiCompletionResult("a, b):", new TokenUsage(3, 2, 5), "stop", new ProviderInvocationMetadata("azure-openai", "azure-openai", 1)));
    }

    private sealed class SecretLeakingProvider : IAiProvider, ICompletionProvider
    {
        public string ProviderName => "azure-openai";
        public Task<AiChatResult> CompleteAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiCompletionResult> CompleteTextAsync(GatewayModel model, AiCompletionRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiCompletionResult("key = AKIAIOSFODNN7EXAMPLE", new TokenUsage(1, 1, 2), "stop", new ProviderInvocationMetadata("azure-openai", "azure-openai", 1)));
    }

    private sealed class ChatOnlyProvider : IAiProvider
    {
        public string ProviderName => "azure-openai";
        public Task<AiChatResult> CompleteAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AllowCompletionPolicy(GatewayModel model) : IPolicyEngine
    {
        public Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>([]);
        public Task<PolicyDecision> AuthorizeCompletionAsync(UserContext user, RequestContext context, AiCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyDecision(true, "ALLOW", model) { RuleId = PolicyRuleIds.Allow });
    }

    private sealed class DenyCompletionPolicy : IPolicyEngine
    {
        public Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>([]);
        public Task<PolicyDecision> AuthorizeCompletionAsync(UserContext user, RequestContext context, AiCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyDecision(false, "blocked", null, PolicyRuleIds.UserGroupDenied));
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

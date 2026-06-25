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

public sealed class EmbeddingsOrchestratorTests
{
    [Fact]
    public async Task Valid_request_returns_embeddings_and_audits_input_count()
    {
        var audit = new CapturingAuditLogger();
        var orchestrator = Build(audit, new AllowEmbeddingsPolicy(Model()), new FakeEmbeddingProvider());

        var (status, body) = await Run(orchestrator, Json(new[] { "alpha", "beta" }));

        Assert.Equal(StatusCodes.Status200OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("list", doc.RootElement.GetProperty("object").GetString());
        var data = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(2, data.Count);
        Assert.Equal(0.5f, data[0].GetProperty("embedding")[0].GetSingle());

        var ev = Assert.Single(audit.Events);
        Assert.Equal("ALLOW", ev.Decision);
        Assert.Equal("AI_EMBEDDINGS", ev.EventType);
        Assert.Equal(2, ev.EmbeddingInputCount);
    }

    [Fact]
    public async Task String_input_is_accepted()
    {
        var (status, body) = await Run(Build(new CapturingAuditLogger(), new AllowEmbeddingsPolicy(Model()), new FakeEmbeddingProvider()), Json("just one"));
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Single(JsonDocument.Parse(body).RootElement.GetProperty("data").EnumerateArray());
    }

    [Theory]
    [InlineData("\"\"", "input_required")]            // empty string
    [InlineData("[]", "input_required")]              // empty array
    [InlineData("[123]", "unsupported_input")]        // token-array input
    [InlineData("123", "input_required")]             // wrong type
    public async Task Invalid_input_is_rejected(string inputJson, string expectedCode)
    {
        var (status, body) = await Run(
            Build(new CapturingAuditLogger(), new AllowEmbeddingsPolicy(Model()), new FakeEmbeddingProvider()),
            JsonDocument.Parse(inputJson).RootElement.Clone());

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains(expectedCode, body);
    }

    [Fact]
    public async Task Missing_model_is_rejected()
    {
        var (status, body) = await Run(
            Build(new CapturingAuditLogger(), new AllowEmbeddingsPolicy(Model()), new FakeEmbeddingProvider()),
            Json("x"), model: "");

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("model_required", body);
    }

    [Fact]
    public async Task Too_many_inputs_is_rejected()
    {
        var options = new GatewayOptions { RequestValidation = new RequestValidationOptions { MaxEmbeddingInputs = 1 } };
        var (status, body) = await Run(
            Build(new CapturingAuditLogger(), new AllowEmbeddingsPolicy(Model()), new FakeEmbeddingProvider(), options),
            Json(new[] { "a", "b" }), options: options);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("too_many_inputs", body);
    }

    [Fact]
    public async Task Policy_denial_returns_403()
    {
        var (status, body) = await Run(
            Build(new CapturingAuditLogger(), new DenyEmbeddingsPolicy(), new FakeEmbeddingProvider()),
            Json("x"));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains("USER_GROUP_DENIED", body);
    }

    [Fact]
    public async Task Provider_without_embedding_capability_is_not_supported()
    {
        // A provider that only implements IAiProvider (no IEmbeddingProvider) → fail closed (501).
        var (status, body) = await Run(
            Build(new CapturingAuditLogger(), new AllowEmbeddingsPolicy(Model()), new ChatOnlyProvider()),
            Json("x"));

        Assert.Equal(StatusCodes.Status501NotImplemented, status);
        Assert.Contains("unsupported_model", body);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static EmbeddingsOrchestrator Build(IAuditLogger audit, IPolicyEngine policy, IAiProvider provider, GatewayOptions? options = null)
    {
        options ??= new GatewayOptions();
        return new EmbeddingsOrchestrator(
            new UserContextFactory(MsOptions.Create(options)),
            new RequestContextFactory(),
            policy,
            [provider],
            audit,
            new OpenAiErrorMapper(),
            new NoOpMetrics(),
            MsOptions.Create(options),
            new ResiliencePipelineBuilder().Build());
    }

    private static async Task<(int Status, string Body)> Run(EmbeddingsOrchestrator orchestrator, JsonElement input, string model = "embed-model", GatewayOptions? options = null)
    {
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace" };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user")], "test"));
        var request = new OpenAiEmbeddingsRequest { Model = model, Input = input };

        var result = await orchestrator.EmbedAsync(httpContext, request, CancellationToken.None);
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        return (httpContext.Response.StatusCode, await new StreamReader(httpContext.Response.Body).ReadToEndAsync());
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static GatewayModel Model() => new() { Id = "embed-model", Alias = "embed-model", ProviderName = "azure-openai", SupportsEmbeddings = true, MaxInputCharacters = 100000 };

    private sealed class FakeEmbeddingProvider : IAiProvider, IEmbeddingProvider
    {
        public string ProviderName => "azure-openai";
        public Task<AiChatResult> CompleteAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AiEmbeddingsResult> EmbedAsync(GatewayModel model, AiEmbeddingsRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiEmbeddingsResult(
                request.Inputs.Select((_, i) => new AiEmbedding(i, [0.5f, 0.25f])).ToList(),
                new TokenUsage(7, 0, 7),
                new ProviderInvocationMetadata("azure-openai", "azure-openai", 1)));
    }

    private sealed class ChatOnlyProvider : IAiProvider
    {
        public string ProviderName => "azure-openai";
        public Task<AiChatResult> CompleteAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AllowEmbeddingsPolicy(GatewayModel model) : IPolicyEngine
    {
        public Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>([]);
        public Task<PolicyDecision> AuthorizeEmbeddingsAsync(UserContext user, RequestContext context, AiEmbeddingsRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyDecision(true, "ALLOW", model) { RuleId = PolicyRuleIds.Allow });
    }

    private sealed class DenyEmbeddingsPolicy : IPolicyEngine
    {
        public Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>([]);
        public Task<PolicyDecision> AuthorizeEmbeddingsAsync(UserContext user, RequestContext context, AiEmbeddingsRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyDecision(false, "blocked", null, PolicyRuleIds.UserGroupDenied));
    }

    private sealed class CapturingAuditLogger : IAuditLogger
    {
        public List<GatewayAuditEvent> Events { get; } = [];
        public void Write(GatewayAuditEvent auditEvent) => Events.Add(auditEvent);
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

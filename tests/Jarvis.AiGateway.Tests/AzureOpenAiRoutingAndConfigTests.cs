using System.Net;
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

public sealed class AzureOpenAiRoutingAndConfigTests
{
    // ── Provider routing ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Orchestrator_routes_to_provider_matching_model_provider_name()
    {
        var model = Model("azure-openai");
        var orchestrator = Orchestrator(model,
            new FakeProvider("aws-bedrock", "from-bedrock"),
            new FakeProvider("azure-openai", "from-azure"));

        var (body, httpContext) = await Run(orchestrator);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("from-azure", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Orchestrator_returns_501_when_no_provider_matches_and_more_than_one_registered()
    {
        var model = Model("nonexistent-provider");
        var orchestrator = Orchestrator(model,
            new FakeProvider("aws-bedrock", "a"),
            new FakeProvider("azure-openai", "b"));

        var (_, httpContext) = await Run(orchestrator);

        Assert.Equal(StatusCodes.Status501NotImplemented, httpContext.Response.StatusCode);
    }

    // ── Error mapping ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, StatusCodes.Status502BadGateway, "provider_validation_error")]
    [InlineData(HttpStatusCode.Unauthorized, StatusCodes.Status502BadGateway, "provider_auth_error")]
    [InlineData(HttpStatusCode.Forbidden, StatusCodes.Status502BadGateway, "provider_auth_error")]
    [InlineData(HttpStatusCode.TooManyRequests, StatusCodes.Status429TooManyRequests, "provider_throttled")]
    [InlineData(HttpStatusCode.InternalServerError, StatusCodes.Status503ServiceUnavailable, "provider_unavailable")]
    [InlineData(HttpStatusCode.BadGateway, StatusCodes.Status503ServiceUnavailable, "provider_unavailable")]
    [InlineData((HttpStatusCode)418, StatusCodes.Status502BadGateway, "provider_error")]
    public void Azure_exception_maps_to_stable_codes(HttpStatusCode status, int expectedStatus, string expectedCode)
    {
        var mapping = new OpenAiErrorMapper().MapException(new AzureOpenAiException(status));

        Assert.Equal(expectedStatus, mapping.StatusCode);
        Assert.Equal(expectedCode, mapping.Response.Error.Code);
        // Provider-internal HTTP status is never echoed verbatim to the caller.
        Assert.DoesNotContain(((int)status).ToString(), mapping.Response.Error.Message);
    }

    // ── Provider-aware validation ─────────────────────────────────────────────────

    [Fact]
    public void Validator_accepts_azure_model_with_deployment_and_no_bedrock_id()
    {
        var result = new GatewayOptionsValidator().Validate(null, Options(new ModelRouteOptions
        {
            Alias = "chat",
            ProviderName = "azure-openai",
            AzureDeploymentName = "jarvis2-chat"
        }));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validator_rejects_azure_model_missing_deployment()
    {
        var result = new GatewayOptionsValidator().Validate(null, Options(new ModelRouteOptions
        {
            Alias = "chat",
            ProviderName = "azure-openai"
        }));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("AzureDeploymentName"));
    }

    [Fact]
    public void Validator_rejects_unknown_provider()
    {
        var result = new GatewayOptionsValidator().Validate(null, Options(new ModelRouteOptions
        {
            Alias = "chat",
            ProviderName = "openai-direct",
            BedrockModelId = "x"
        }));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("unknown ProviderName"));
    }

    [Fact]
    public void Validator_still_requires_bedrock_id_for_bedrock_models()
    {
        var result = new GatewayOptionsValidator().Validate(null, Options(new ModelRouteOptions
        {
            Alias = "general",
            ProviderName = "aws-bedrock"
        }));

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("BedrockModelId"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static GatewayOptions Options(ModelRouteOptions model) => new()
    {
        IdentityBroker = new IdentityBrokerOptions { Enabled = false },
        Models = [model]
    };

    private static GatewayModel Model(string providerName) => new()
    {
        Id = "general",
        Alias = "general",
        ProviderName = providerName,
        BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0",
        AzureDeploymentName = "jarvis2-chat",
        SupportsConverse = true,
        OutputModalities = ["TEXT"],
        MaxOutputTokens = 100
    };

    private static ChatCompletionOrchestrator Orchestrator(GatewayModel model, params IAiProvider[] providers)
    {
        var options = new GatewayOptions();
        return new ChatCompletionOrchestrator(
            new UserContextFactory(MsOptions.Create(options)),
            new RequestContextFactory(),
            new OpenAiChatRequestValidator(MsOptions.Create(options)),
            new AllowPolicyEngine(model),
            providers,
            new RegexContentRedactor(MsOptions.Create(options)),
            new CapturingAuditLogger(),
            new OpenAiErrorMapper(),
            new NoOpMetrics(),
            MsOptions.Create(options),
            new ResiliencePipelineBuilder().Build());
    }

    private static async Task<(string Body, HttpContext Context)> Run(ChatCompletionOrchestrator orchestrator)
    {
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace-id" };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user")], "test"));
        var request = new OpenAiChatCompletionRequest
        {
            Model = "general",
            Messages = [new OpenAiMessage { Role = "user", Content = JsonSerializer.SerializeToElement("hello") }]
        };

        var result = await orchestrator.CompleteAsync(httpContext, request, CancellationToken.None);
        httpContext.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        return (await new StreamReader(httpContext.Response.Body).ReadToEndAsync(), httpContext);
    }

    private sealed class FakeProvider(string name, string text) : IAiProvider
    {
        public string ProviderName => name;

        public Task<AiChatResult> CompleteAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiChatResult(text, new TokenUsage(1, 1, 2), "stop", new ProviderInvocationMetadata(name, name, 1)));

        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class AllowPolicyEngine(GatewayModel model) : IPolicyEngine
    {
        public Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyDecision(true, "ALLOW", model) { RuleId = PolicyRuleIds.Allow });

        public Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GatewayModel>>([model]);
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

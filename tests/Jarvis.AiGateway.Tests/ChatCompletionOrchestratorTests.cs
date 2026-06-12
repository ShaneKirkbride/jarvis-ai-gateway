using System.Security.Claims;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

public sealed class ChatCompletionOrchestratorTests
{
    [Fact]
    public async Task Provider_timeout_returns_gateway_timeout_and_writes_audit()
    {
        var options = new GatewayOptions { ProviderTimeoutSeconds = 1 };
        var auditLogger = new CapturingAuditLogger();
        var orchestrator = new ChatCompletionOrchestrator(
            new UserContextFactory(MsOptions.Create(options)),
            new RequestContextFactory(),
            new OpenAiChatRequestValidator(MsOptions.Create(options)),
            new AllowPolicyEngine(Model()),
            [new SlowStrategy()],
            new RegexContentRedactor(MsOptions.Create(options)),
            auditLogger,
            new OpenAiErrorMapper(),
            MsOptions.Create(options));
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace-id" };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user")], "test"));
        var request = new OpenAiChatCompletionRequest
        {
            Model = "general",
            Messages = [new OpenAiMessage { Role = "user", Content = JsonSerializer.SerializeToElement("hello") }]
        };

        var result = await orchestrator.CompleteAsync(httpContext, request, CancellationToken.None);

        var json = await ExecuteResult(result, httpContext);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, httpContext.Response.StatusCode);
        Assert.Contains("provider_timeout", json);
        Assert.Equal("ERROR", auditLogger.Events.Single().Decision);
        Assert.Equal("provider_timeout", auditLogger.Events.Single().ErrorType);
    }

    private static async Task<string> ExecuteResult(IResult result, HttpContext context)
    {
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static GatewayModel Model() => new()
    {
        Id = "general",
        Alias = "general",
        BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0",
        ProviderName = "fake",
        SupportsConverse = true,
        OutputModalities = ["TEXT"],
        MaxOutputTokens = 10
    };

    private sealed class AllowPolicyEngine(GatewayModel model) : IPolicyEngine
    {
        public Task<PolicyDecision> AuthorizeAsync(UserContext user, RequestContext context, AiChatRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PolicyDecision(true, "ALLOW", model) { RuleId = PolicyRuleIds.Allow });

        public Task<IReadOnlyList<GatewayModel>> GetVisibleModelsAsync(UserContext user, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GatewayModel>>([model]);
    }

    private sealed class SlowStrategy : IBedrockInvocationStrategy
    {
        public string Name => "slow";
        public bool CanHandle(GatewayModel model, AiChatRequest request) => true;

        public async Task<AiChatResult> InvokeAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new AiChatResult("never", new TokenUsage(0, 0, 0), "stop", new ProviderInvocationMetadata("fake", Name, 0));
        }
    }

    private sealed class CapturingAuditLogger : IAuditLogger
    {
        public List<GatewayAuditEvent> Events { get; } = [];
        public void Write(GatewayAuditEvent auditEvent) => Events.Add(auditEvent);
    }
}

using System.Security.Claims;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// Exercises the in-scope orchestrator tool path end-to-end (validate → policy → provider →
/// response mapping → outbound redaction → audit) with a fake provider returning a tool call.
/// </summary>
public sealed class ToolCallingOrchestratorTests
{
    [Fact]
    public async Task Tool_call_result_is_returned_audited_and_arguments_redacted()
    {
        var options = new GatewayOptions();
        var audit = new CapturingAuditLogger();
        var orchestrator = new ChatCompletionOrchestrator(
            new UserContextFactory(MsOptions.Create(options)),
            new RequestContextFactory(),
            new OpenAiChatRequestValidator(MsOptions.Create(options)),
            new AllowPolicyEngine(ToolsModel()),
            [new ToolCallStrategy()],
            new RegexContentRedactor(MsOptions.Create(options)),
            audit,
            new OpenAiErrorMapper(),
            MsOptions.Create(options));

        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace" };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user")], "test"));
        var request = new OpenAiChatCompletionRequest
        {
            Model = "general",
            Messages = [new OpenAiMessage { Role = "user", Content = JsonSerializer.SerializeToElement("weather?") }],
            Tools = [new OpenAiTool { Function = new OpenAiFunctionDefinition { Name = "get_weather", Parameters = JsonSerializer.SerializeToElement(new { type = "object" }) } }]
        };

        var result = await orchestrator.CompleteAsync(httpContext, request, CancellationToken.None);
        var body = await ExecuteResult(result, httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        var toolCall = choice.GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal("get_weather", toolCall.GetProperty("function").GetProperty("name").GetString());
        // Secret echoed into the tool-call arguments is redacted outbound.
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", body);

        var ev = Assert.Single(audit.Events);
        Assert.Equal("ALLOW", ev.Decision);
        Assert.Equal(1, ev.ToolsOffered);
        Assert.Equal(1, ev.ToolCallsReturned);
        Assert.Equal(["get_weather"], ev.ToolCallNames);
    }

    private static async Task<string> ExecuteResult(IResult result, HttpContext context)
    {
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static GatewayModel ToolsModel() => new()
    {
        Id = "general",
        Alias = "general",
        ProviderName = "fake",
        BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0",
        SupportsConverse = true,
        SupportsTools = true,
        OutputModalities = ["TEXT"],
        MaxOutputTokens = 100
    };

    private sealed class ToolCallStrategy : IBedrockInvocationStrategy
    {
        public string Name => "fake-tools";
        public bool CanHandle(GatewayModel model, AiChatRequest request) => true;
        public Task<AiChatResult> InvokeAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiChatResult(
                string.Empty,
                new TokenUsage(3, 2, 5),
                "tool_use",
                new ProviderInvocationMetadata("fake", Name, 1),
                [new AiToolCall("call_1", "get_weather", "{\"note\":\"AKIAIOSFODNN7EXAMPLE\"}")]));
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
        public List<GatewayAuditEvent> Events { get; } = [];
        public void Write(GatewayAuditEvent auditEvent) => Events.Add(auditEvent);
        public void WriteIdentity(IdentityAuditEvent auditEvent) { }
    }
}

using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class PolicyAndInvocationTests
{
    [Fact]
    public async Task Itar_request_blocks_non_itar_model()
    {
        var options = new GatewayOptions
        {
            ItarApprovedWorkspaceIds = ["itar-approved"],
            Models =
            [
                new ModelRouteOptions
                {
                    Alias = "general",
                    BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0",
                    Enabled = true,
                    ItarApproved = false,
                    RequiredGroups = ["AI-General-Users"]
                }
            ]
        };
        var registry = CreateRegistry(options, Discovered("anthropic.claude-3-haiku-20240307-v1:0", "Anthropic"));
        var policy = new PolicyEngine(Microsoft.Extensions.Options.Options.Create(options), registry);
        var user = new UserContext("u1", "u1@example.test", new HashSet<string>(["AI-General-Users"]), new Dictionary<string, string>());
        var context = new RequestContext("r1", "c1", "itar-approved", "ITAR", true);

        var decision = await policy.AuthorizeAsync(user, context, Request("general"), CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains("non-ITAR-approved", decision.Reason);
    }

    [Fact]
    public void Converse_capable_model_selects_converse_strategy()
    {
        var model = new GatewayModel { Id = "alias", Alias = "alias", BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0", SupportsConverse = true };
        var strategy = new BedrockConverseInvocationStrategy(null!, new NullRedactor(), Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), NullLogger<BedrockConverseInvocationStrategy>.Instance);

        Assert.True(strategy.CanHandle(model, Request("alias")));
    }

    [Fact]
    public void Non_converse_model_without_adapter_has_clear_unsupported_error()
    {
        var model = new GatewayModel { Id = "unknown", Alias = "unknown", BedrockModelId = "unknown.provider-model", ProviderName = "Unknown", SupportsConverse = false };
        var strategy = new BedrockInvokeModelTextInvocationStrategy(null!, [], NullLogger<BedrockInvokeModelTextInvocationStrategy>.Instance);

        Assert.False(strategy.CanHandle(model, Request("unknown")));
        Assert.Equal("Model is discovered but not supported by this gateway invocation adapter yet.", BedrockInvokeModelTextInvocationStrategy.UnsupportedAdapterMessage);
    }

    [Fact]
    public void Audit_event_carries_resolved_model_metadata()
    {
        var audit = new GatewayAuditEvent
        {
            RequestedModelAlias = "general",
            ResolvedBedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0",
            Provider = "Anthropic",
            InvocationStrategy = "converse",
            SupportsConverse = true,
            StreamingSupported = true,
            PolicyDecision = "ALLOW",
            TokenEstimate = 42
        };

        Assert.Equal("Anthropic", audit.Provider);
        Assert.Equal("converse", audit.InvocationStrategy);
        Assert.True(audit.SupportsConverse);
        Assert.Equal(42, audit.TokenEstimate);
    }

    private static ModelRegistry CreateRegistry(GatewayOptions options, params DiscoveredBedrockModel[] discovered) => new(
        new FakeDiscovery(discovered),
        [new MetaLlamaInvokeModelPayloadAdapter()],
        Microsoft.Extensions.Options.Options.Create(options),
        NullLogger<ModelRegistry>.Instance);

    private static OpenAiChatCompletionRequest Request(string model)
    {
        using var doc = JsonDocument.Parse("\"hello\"");
        return new OpenAiChatCompletionRequest
        {
            Model = model,
            Messages = [new OpenAiMessage { Role = "user", Content = doc.RootElement.Clone() }]
        };
    }

    private static DiscoveredBedrockModel Discovered(string id, string provider) => new()
    {
        ModelId = id,
        ModelName = id,
        ProviderName = provider,
        InputModalities = ["TEXT"],
        OutputModalities = ["TEXT"],
        LifecycleStatus = "ACTIVE",
        SupportsConverse = BedrockModelCapabilities.SupportsConverse(id, provider)
    };

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredBedrockModel> models) : IBedrockModelDiscoveryService
    {
        public Task<IReadOnlyList<DiscoveredBedrockModel>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult(models);
    }

    private sealed class NullRedactor : IContentRedactor
    {
        public RedactionResult Redact(string text) => new(text, 0);
    }
}

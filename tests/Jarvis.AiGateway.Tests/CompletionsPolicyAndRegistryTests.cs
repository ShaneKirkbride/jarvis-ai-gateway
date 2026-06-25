using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

public sealed class CompletionsPolicyAndRegistryTests
{
    [Fact]
    public async Task Registry_returns_only_fim_models()
    {
        var options = new GatewayOptions
        {
            Models =
            [
                new ModelRouteOptions { Alias = "fim", ProviderName = "azure-openai", AzureDeploymentName = "fim-deploy", SupportsFim = true },
                new ModelRouteOptions { Alias = "chat", BedrockModelId = "anthropic.x", SupportsFim = false }
            ]
        };
        var registry = new ModelRegistry(new EmptyDiscovery(), [], MsOptions.Create(options), NullLogger<ModelRegistry>.Instance);

        Assert.Equal("fim", Assert.Single(await registry.GetCompletionModelsAsync(default)).Id);
        Assert.NotNull(await registry.FindCompletionModelAsync("fim", default));
        Assert.Null(await registry.FindCompletionModelAsync("chat", default));
    }

    [Fact]
    public void Capability_mapper_reflects_supports_fim()
    {
        Assert.True(ModelCapabilityMapper.ToModelInfo(new GatewayModel { Id = "f", ProviderName = "azure-openai", SupportsFim = true }).SupportsFim);
        Assert.False(ModelCapabilityMapper.ToModelInfo(new GatewayModel { Id = "c", ProviderName = "azure-openai" }).SupportsFim);
    }

    [Fact]
    public async Task Completion_authorization_enforces_group_and_model_resolution()
    {
        var engine = new PolicyEngine(MsOptions.Create(new GatewayOptions()), new FakeRegistry(FimModel()));

        Assert.True((await engine.AuthorizeCompletionAsync(UserInGroups("G"), Ctx(), Req("code"), default)).Allowed);
        Assert.Equal(PolicyRuleIds.UserGroupDenied, (await engine.AuthorizeCompletionAsync(UserInGroups("OTHER"), Ctx(), Req("code"), default)).RuleId);
        Assert.Equal(PolicyRuleIds.ModelNotFound, (await new PolicyEngine(MsOptions.Create(new GatewayOptions()), new FakeRegistry(null)).AuthorizeCompletionAsync(UserInGroups("G"), Ctx(), Req("code"), default)).RuleId);
    }

    [Fact]
    public async Task Completion_prompt_and_suffix_run_through_blocked_pattern_rule()
    {
        var options = new GatewayOptions { BlockedPromptPatterns = ["forbidden"] };
        var engine = new PolicyEngine(MsOptions.Create(options), new FakeRegistry(FimModel()));

        var result = await engine.AuthorizeCompletionAsync(UserInGroups("G"), Ctx(), new AiCompletionRequest("fim", "ok prefix", "forbidden suffix", null, null, null, []), default);

        Assert.False(result.Allowed);
        Assert.Equal(PolicyRuleIds.PromptBlockedPattern, result.RuleId);
    }

    [Fact]
    public async Task Visible_completion_models_are_group_filtered()
    {
        var engine = new PolicyEngine(MsOptions.Create(new GatewayOptions()), new FakeRegistry(FimModel()));
        Assert.Single(await engine.GetVisibleCompletionModelsAsync(UserInGroups("G"), default));
        Assert.Empty(await engine.GetVisibleCompletionModelsAsync(UserInGroups("OTHER"), default));
    }

    private static GatewayModel FimModel() => new()
    {
        Id = "fim",
        Alias = "fim",
        ProviderName = "azure-openai",
        AzureDeploymentName = "fim-deploy",
        SupportsFim = true,
        RequiredGroups = ["G"],
        MaxInputCharacters = 100000,
        OutputModalities = ["TEXT"]
    };

    private static AiCompletionRequest Req(string prompt) => new("fim", prompt, null, null, null, null, []);
    private static RequestContext Ctx() => new("rid", "cid", "ws", "GENERAL", false);
    private static UserContext UserInGroups(params string[] groups) =>
        new("u", "u@example.test", new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>());

    private sealed class EmptyDiscovery : IBedrockModelDiscoveryService
    {
        public Task<IReadOnlyList<DiscoveredBedrockModel>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscoveredBedrockModel>>([]);
    }

    private sealed class FakeRegistry(GatewayModel? model) : IModelRegistry
    {
        public Task<IReadOnlyList<GatewayModel>> GetChatModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>([]);
        public Task<GatewayModel?> FindChatModelAsync(string requestedModel, CancellationToken cancellationToken) => Task.FromResult<GatewayModel?>(null);
        public Task<IReadOnlyList<GatewayModel>> GetCompletionModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>(model is null ? [] : [model]);
        public Task<GatewayModel?> FindCompletionModelAsync(string requestedModel, CancellationToken cancellationToken) => Task.FromResult(model);
    }
}

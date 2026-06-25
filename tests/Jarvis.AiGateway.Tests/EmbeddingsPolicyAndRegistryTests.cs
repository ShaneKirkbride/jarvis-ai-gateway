using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

public sealed class EmbeddingsPolicyAndRegistryTests
{
    // ── Registry ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registry_returns_only_embedding_models()
    {
        var options = new GatewayOptions
        {
            Models =
            [
                new ModelRouteOptions { Alias = "embed", ProviderName = "azure-openai", AzureDeploymentName = "text-embed", SupportsEmbeddings = true },
                new ModelRouteOptions { Alias = "chat", BedrockModelId = "anthropic.x", SupportsEmbeddings = false }
            ]
        };
        var registry = Registry(options);

        var models = await registry.GetEmbeddingModelsAsync(default);

        Assert.Equal("embed", Assert.Single(models).Id);
        Assert.NotNull(await registry.FindEmbeddingModelAsync("embed", default));
        Assert.Null(await registry.FindEmbeddingModelAsync("chat", default));
        Assert.Null(await registry.FindEmbeddingModelAsync("", default));
    }

    [Fact]
    public void Capability_mapper_reflects_supports_embeddings()
    {
        Assert.True(ModelCapabilityMapper.ToModelInfo(new GatewayModel { Id = "e", ProviderName = "azure-openai", SupportsEmbeddings = true }).SupportsEmbeddings);
        Assert.False(ModelCapabilityMapper.ToModelInfo(new GatewayModel { Id = "c", ProviderName = "azure-openai" }).SupportsEmbeddings);
    }

    // ── Policy ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Embeddings_authorization_enforces_group_and_model_resolution()
    {
        var engine = new PolicyEngine(MsOptions.Create(new GatewayOptions()), new FakeRegistry(EmbeddingModel()));

        Assert.True((await engine.AuthorizeEmbeddingsAsync(UserInGroups("G"), Ctx(), Req("hi"), default)).Allowed);

        var deny = await engine.AuthorizeEmbeddingsAsync(UserInGroups("OTHER"), Ctx(), Req("hi"), default);
        Assert.False(deny.Allowed);
        Assert.Equal(PolicyRuleIds.UserGroupDenied, deny.RuleId);

        var notFound = await new PolicyEngine(MsOptions.Create(new GatewayOptions()), new FakeRegistry(null))
            .AuthorizeEmbeddingsAsync(UserInGroups("G"), Ctx(), Req("hi"), default);
        Assert.Equal(PolicyRuleIds.ModelNotFound, notFound.RuleId);
    }

    [Fact]
    public async Task Embeddings_inputs_run_through_blocked_pattern_rule()
    {
        var options = new GatewayOptions { BlockedPromptPatterns = ["forbidden"] };
        var engine = new PolicyEngine(MsOptions.Create(options), new FakeRegistry(EmbeddingModel()));

        var result = await engine.AuthorizeEmbeddingsAsync(UserInGroups("G"), Ctx(), Req("this is forbidden text"), default);

        Assert.False(result.Allowed);
        Assert.Equal(PolicyRuleIds.PromptBlockedPattern, result.RuleId);
    }

    [Fact]
    public async Task Visible_embedding_models_are_group_filtered()
    {
        var engine = new PolicyEngine(MsOptions.Create(new GatewayOptions()), new FakeRegistry(EmbeddingModel()));

        Assert.Single(await engine.GetVisibleEmbeddingModelsAsync(UserInGroups("G"), default));
        Assert.Empty(await engine.GetVisibleEmbeddingModelsAsync(UserInGroups("OTHER"), default));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static ModelRegistry Registry(GatewayOptions options) =>
        new(new EmptyDiscovery(), [], MsOptions.Create(options), NullLogger<ModelRegistry>.Instance);

    private static GatewayModel EmbeddingModel() => new()
    {
        Id = "embed",
        Alias = "embed",
        ProviderName = "azure-openai",
        AzureDeploymentName = "text-embed",
        SupportsEmbeddings = true,
        RequiredGroups = ["G"],
        MaxInputCharacters = 100000,
        OutputModalities = ["TEXT"]
    };

    private static AiEmbeddingsRequest Req(string input) => new("embed", [input], null);

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
        public Task<IReadOnlyList<GatewayModel>> GetEmbeddingModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>(model is null ? [] : [model]);
        public Task<GatewayModel?> FindEmbeddingModelAsync(string requestedModel, CancellationToken cancellationToken) => Task.FromResult(model);
    }
}

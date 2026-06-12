using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class ModelRegistryTests
{
    [Fact]
    public async Task Configured_alias_resolution_returns_friendly_alias()
    {
        var registry = CreateRegistry(new GatewayOptions
        {
            Models = [Configured("jarvis-govcloud-general", "anthropic.claude-3-haiku-20240307-v1:0")]
        }, Discovered("anthropic.claude-3-haiku-20240307-v1:0", provider: "Anthropic"));

        var model = await registry.FindChatModelAsync("jarvis-govcloud-general", CancellationToken.None);

        Assert.NotNull(model);
        Assert.Equal("jarvis-govcloud-general", model!.Id);
        Assert.Equal("anthropic.claude-3-haiku-20240307-v1:0", model.BedrockModelId);
        Assert.True(model.SupportsConverse);
    }

    [Fact]
    public async Task Raw_model_ids_are_not_exposed_by_default()
    {
        var registry = CreateRegistry(new GatewayOptions(), Discovered("meta.llama3-8b-instruct-v1:0", provider: "Meta"));

        var models = await registry.GetChatModelsAsync(CancellationToken.None);

        Assert.Empty(models);
    }

    [Fact]
    public async Task Policy_blocks_discovered_model_when_not_allowlisted()
    {
        var options = new GatewayOptions
        {
            ModelDiscovery = new ModelDiscoveryOptions { ExposeRawBedrockModelIds = true },
            Policy = new GatewayPolicyOptions
            {
                RequireExplicitModelAllowlist = true,
                AllowDiscoveredModelsForNonItar = false
            }
        };
        var registry = CreateRegistry(options, Discovered("meta.llama3-8b-instruct-v1:0", provider: "Meta"));

        var models = await registry.GetChatModelsAsync(CancellationToken.None);

        Assert.Empty(models);
    }

    [Fact]
    public async Task Allowed_raw_discovered_model_must_be_text_chat_candidate()
    {
        var options = new GatewayOptions
        {
            ModelDiscovery = new ModelDiscoveryOptions { ExposeRawBedrockModelIds = true },
            Policy = new GatewayPolicyOptions
            {
                RequireExplicitModelAllowlist = true,
                AllowedModelIdPatterns = ["^meta\\.llama"]
            }
        };
        var registry = CreateRegistry(options,
            Discovered("meta.llama3-8b-instruct-v1:0", provider: "Meta"),
            Discovered("amazon.titan-embed-text-v2:0", provider: "Amazon", outputs: ["EMBEDDING"]),
            Discovered("amazon.titan-image-generator-v2:0", provider: "Amazon", outputs: ["IMAGE"]));

        var models = await registry.GetChatModelsAsync(CancellationToken.None);

        Assert.Single(models);
        Assert.Equal("meta.llama3-8b-instruct-v1:0", models[0].Id);
    }

    private static ModelRegistry CreateRegistry(GatewayOptions options, params DiscoveredBedrockModel[] discovered)
    {
        return new ModelRegistry(
            new FakeDiscovery(discovered),
            [new MetaLlamaInvokeModelPayloadAdapter(), new AmazonTitanTextInvokeModelPayloadAdapter(), new MistralInvokeModelPayloadAdapter()],
            Options.Create(options),
            NullLogger<ModelRegistry>.Instance);
    }

    private static ModelRouteOptions Configured(string alias, string modelId) => new()
    {
        Alias = alias,
        BedrockModelId = modelId,
        Enabled = true,
        RequiredGroups = ["AI-General-Users"]
    };

    private static DiscoveredBedrockModel Discovered(string id, string provider, string[]? outputs = null) => new()
    {
        ModelId = id,
        ModelName = id,
        ProviderName = provider,
        InputModalities = ["TEXT"],
        OutputModalities = outputs ?? ["TEXT"],
        InferenceTypesSupported = ["ON_DEMAND"],
        LifecycleStatus = "ACTIVE",
        ResponseStreamingSupported = true,
        SupportsConverse = BedrockModelCapabilities.SupportsConverse(id, provider)
    };

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredBedrockModel> models) : IBedrockModelDiscoveryService
    {
        public Task<IReadOnlyList<DiscoveredBedrockModel>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult(models);
    }
}

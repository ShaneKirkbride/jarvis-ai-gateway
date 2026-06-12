using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class PolicyEngineMatrixTests
{
    [Fact]
    public async Task Visible_models_only_include_models_for_user_groups()
    {
        var options = Options(Model("open", groups: []), Model("restricted", groups: ["restricted"]));
        var policy = Policy(options, Discovered("open-id"), Discovered("restricted-id"));

        var visible = await policy.GetVisibleModelsAsync(User(groups: ["other"]), CancellationToken.None);

        Assert.Equal(["open"], visible.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task Authorize_denies_unknown_placeholder_disabled_non_text_and_group_mismatches()
    {
        await AssertDenied(Options(), Request("missing"), "not enabled", null);
        await AssertDenied(Options(Model("placeholder", bedrockId: "REPLACE_WITH_MODEL")), Request("placeholder"), "real Bedrock", Discovered("REPLACE_WITH_MODEL"));
        await AssertDenied(Options(Model("disabled", enabled: false)), Request("disabled"), "not enabled", Discovered("disabled-id"));
        await AssertDenied(Options(Model("image", outputs: ["IMAGE"])), Request("image"), "not enabled", Discovered("image-id", outputs: ["IMAGE"]));
        await AssertDenied(Options(Model("group", groups: ["approved"])), Request("group"), "approved group", Discovered("group-id"), User(groups: ["other"]));
    }

    [Fact]
    public async Task Authorize_denies_prompt_limits_blocked_patterns_and_itar_workspace_mismatch()
    {
        await AssertDenied(Options(Model("small", maxInput: 3)), Request("small", "1234"), "maximum input", Discovered("small-id"));
        await AssertDenied(new GatewayOptions { BlockedPromptPatterns = ["forbidden"], Models = [Model("blocked")] }, Request("blocked", "forbidden text"), "blocked policy", Discovered("blocked-id"));
        await AssertDenied(new GatewayOptions { ItarApprovedWorkspaceIds = ["other"], Models = [Model("itar", itar: true)] }, Request("itar"), "approved ITAR workspace", Discovered("itar-id"), context: new RequestContext("r", "c", "workspace", "ITAR", true));
    }

    [Fact]
    public async Task Authorize_allows_valid_non_itar_and_itar_requests()
    {
        var nonItar = await Policy(Options(Model("general")), Discovered("general-id")).AuthorizeAsync(User(), Context(), Request("general"), CancellationToken.None);
        var itarOptions = new GatewayOptions { ItarApprovedWorkspaceIds = ["workspace"], Models = [Model("itar", itar: true)] };
        var itar = await Policy(itarOptions, Discovered("itar-id")).AuthorizeAsync(User(), new RequestContext("r", "c", "workspace", "ITAR", false), Request("itar"), CancellationToken.None);

        Assert.True(nonItar.Allowed);
        Assert.Equal("ALLOW", nonItar.Reason);
        Assert.True(itar.Allowed);
    }


    [Fact]
    public async Task Authorize_defensively_denies_disabled_and_non_text_models_returned_by_registry()
    {
        var options = new GatewayOptions();
        var disabled = await new PolicyEngine(MsOptions.Create(options), new StaticRegistry(new GatewayModel { Id = "disabled", Alias = "disabled", BedrockModelId = "disabled-id", Enabled = false, OutputModalities = ["TEXT"], RequiredGroups = [] }))
            .AuthorizeAsync(User(), Context(), Request("disabled"), CancellationToken.None);
        var nonText = await new PolicyEngine(MsOptions.Create(options), new StaticRegistry(new GatewayModel { Id = "image", Alias = "image", BedrockModelId = "image-id", Enabled = true, OutputModalities = ["IMAGE"], RequiredGroups = [] }))
            .AuthorizeAsync(User(), Context(), Request("image"), CancellationToken.None);

        Assert.False(disabled.Allowed);
        Assert.Equal("Model is disabled.", disabled.Reason);
        Assert.False(nonText.Allowed);
        Assert.Contains("TEXT output", nonText.Reason);
    }

    private static async Task AssertDenied(GatewayOptions options, OpenAiChatCompletionRequest request, string expectedReason, DiscoveredBedrockModel? discovered, UserContext? user = null, RequestContext? context = null)
    {
        var discoveries = discovered is null ? [] : new[] { discovered };
        var decision = await Policy(options, discoveries).AuthorizeAsync(user ?? User(), context ?? Context(), request, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Contains(expectedReason, decision.Reason);
    }

    private static PolicyEngine Policy(GatewayOptions options, params DiscoveredBedrockModel[] discovered) => new(
        MsOptions.Create(options),
        new ModelRegistry(new FakeDiscovery(discovered), [new MetaLlamaInvokeModelPayloadAdapter(), new AmazonTitanTextInvokeModelPayloadAdapter(), new MistralInvokeModelPayloadAdapter()], MsOptions.Create(options), NullLogger<ModelRegistry>.Instance));

    private static GatewayOptions Options(params ModelRouteOptions[] models) => new() { Models = models.ToList() };

    private static ModelRouteOptions Model(string alias, string? bedrockId = null, bool enabled = true, bool itar = false, string[]? groups = null, int maxInput = 120000, string[]? outputs = null) => new()
    {
        Alias = alias,
        BedrockModelId = bedrockId ?? $"{alias}-id",
        Enabled = enabled,
        ItarApproved = itar,
        RequiredGroups = groups?.ToList() ?? ["AI-General-Users"],
        MaxInputCharacters = maxInput,
        OutputModalities = outputs?.ToList() ?? ["TEXT"],
        SupportsConverse = true
    };

    private static DiscoveredBedrockModel Discovered(string id, string[]? outputs = null) => new()
    {
        ModelId = id,
        ProviderName = "Anthropic",
        OutputModalities = outputs ?? ["TEXT"],
        LifecycleStatus = "ACTIVE",
        SupportsConverse = true
    };

    private static UserContext User(string[]? groups = null) => new("u", "u@example.test", new HashSet<string>(groups ?? ["AI-General-Users"], StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>());

    private static RequestContext Context() => new("r", "c", "workspace", "NON_ITAR", false);

    private static OpenAiChatCompletionRequest Request(string model, string content = "hello") => new()
    {
        Model = model,
        Messages = [new OpenAiMessage { Role = "user", Content = JsonSerializer.SerializeToElement(content) }]
    };

    private sealed class StaticRegistry(GatewayModel model) : IModelRegistry
    {
        public Task<IReadOnlyList<GatewayModel>> GetChatModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GatewayModel>>([model]);
        public Task<GatewayModel?> FindChatModelAsync(string requestedModel, CancellationToken cancellationToken) => Task.FromResult<GatewayModel?>(model);
    }

    private sealed class FakeDiscovery(IReadOnlyList<DiscoveredBedrockModel> models) : IBedrockModelDiscoveryService
    {
        public Task<IReadOnlyList<DiscoveredBedrockModel>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult(models);
    }
}

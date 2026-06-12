using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class DiscoveryFilterTests
{
    [Fact]
    public void Lifecycle_filter_excludes_inactive_models()
    {
        var options = new ModelDiscoveryOptions { IncludeLifecycleStatuses = ["ACTIVE"], IncludeOutputModalities = ["TEXT"] };

        Assert.True(BedrockModelDiscoveryFilter.IsIncluded(Model("active", "ACTIVE", ["TEXT"]), options));
        Assert.False(BedrockModelDiscoveryFilter.IsIncluded(Model("legacy", "LEGACY", ["TEXT"]), options));
    }

    [Fact]
    public void Modality_filter_keeps_text_and_excludes_image_and_embedding()
    {
        var options = new ModelDiscoveryOptions { IncludeLifecycleStatuses = ["ACTIVE"], IncludeOutputModalities = ["TEXT"] };

        Assert.True(BedrockModelDiscoveryFilter.IsIncluded(Model("text", "ACTIVE", ["TEXT"]), options));
        Assert.False(BedrockModelDiscoveryFilter.IsIncluded(Model("image", "ACTIVE", ["IMAGE"]), options));
        Assert.False(BedrockModelDiscoveryFilter.IsIncluded(Model("embedding", "ACTIVE", ["EMBEDDING"]), options));
    }

    [Fact]
    public void Provider_filters_include_and_exclude_providers()
    {
        var include = new ModelDiscoveryOptions { IncludeLifecycleStatuses = ["ACTIVE"], IncludeOutputModalities = ["TEXT"], IncludeProviders = ["Anthropic"] };
        var exclude = new ModelDiscoveryOptions { IncludeLifecycleStatuses = ["ACTIVE"], IncludeOutputModalities = ["TEXT"], ExcludeProviders = ["Meta"] };

        Assert.True(BedrockModelDiscoveryFilter.IsIncluded(Model("claude", "ACTIVE", ["TEXT"], "Anthropic"), include));
        Assert.False(BedrockModelDiscoveryFilter.IsIncluded(Model("llama", "ACTIVE", ["TEXT"], "Meta"), include));
        Assert.False(BedrockModelDiscoveryFilter.IsIncluded(Model("llama", "ACTIVE", ["TEXT"], "Meta"), exclude));
    }

    private static DiscoveredBedrockModel Model(string id, string lifecycle, IReadOnlyList<string> outputs, string provider = "Amazon") => new()
    {
        ModelId = id,
        ModelName = id,
        ProviderName = provider,
        InputModalities = ["TEXT"],
        OutputModalities = outputs,
        LifecycleStatus = lifecycle,
        SupportsConverse = true
    };
}

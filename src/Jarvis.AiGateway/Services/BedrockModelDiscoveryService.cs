using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IBedrockModelDiscoveryService
{
    Task<IReadOnlyList<DiscoveredBedrockModel>> DiscoverAsync(CancellationToken cancellationToken);
}

public sealed class BedrockModelDiscoveryService(
    IAmazonBedrock bedrock,
    IOptions<GatewayOptions> options,
    IMemoryCache cache,
    ILogger<BedrockModelDiscoveryService> logger) : IBedrockModelDiscoveryService
{
    private const string CacheKey = "bedrock-foundation-models";
    private readonly GatewayOptions _options = options.Value;

    public async Task<IReadOnlyList<DiscoveredBedrockModel>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (!_options.ModelDiscovery.Enabled)
        {
            return [];
        }

        if (cache.TryGetValue(CacheKey, out IReadOnlyList<DiscoveredBedrockModel>? cached) && cached is not null)
        {
            return cached;
        }

        var response = await bedrock.ListFoundationModelsAsync(new ListFoundationModelsRequest(), cancellationToken);
        var discovered = (response.ModelSummaries ?? [])
            .Select(Map)
            .Where(m => BedrockModelDiscoveryFilter.IsIncluded(m, _options.ModelDiscovery))
            .OrderBy(m => m.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        cache.Set(CacheKey, discovered, TimeSpan.FromSeconds(Math.Max(1, _options.ModelDiscovery.CacheSeconds)));
        logger.LogInformation("Discovered {ModelCount} Bedrock foundation models in region {AwsRegion} after filtering.", discovered.Count, _options.AwsRegion);
        return discovered;
    }

    private static DiscoveredBedrockModel Map(FoundationModelSummary summary)
    {
        var lifecycleStatus = summary.ModelLifecycle?.Status?.ToString() ?? string.Empty;
        var inputModalities = summary.InputModalities?.Select(v => v?.ToString() ?? string.Empty).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray() ?? [];
        var outputModalities = summary.OutputModalities?.Select(v => v?.ToString() ?? string.Empty).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray() ?? [];
        var inferenceTypes = summary.InferenceTypesSupported?.Select(v => v?.ToString() ?? string.Empty).Where(v => !string.IsNullOrWhiteSpace(v)).ToArray() ?? [];

        return new DiscoveredBedrockModel
        {
            ModelId = summary.ModelId ?? string.Empty,
            ModelName = summary.ModelName ?? summary.ModelId ?? string.Empty,
            ProviderName = summary.ProviderName ?? string.Empty,
            InputModalities = inputModalities,
            OutputModalities = outputModalities,
            InferenceTypesSupported = inferenceTypes,
            ResponseStreamingSupported = summary.ResponseStreamingSupported == true,
            LifecycleStatus = lifecycleStatus,
            SupportsConverse = BedrockModelCapabilities.SupportsConverse(summary.ModelId ?? string.Empty, summary.ProviderName)
        };
    }

}

public static class BedrockModelDiscoveryFilter
{
    public static bool IsIncluded(DiscoveredBedrockModel model, ModelDiscoveryOptions options)
    {
        return AllowedLifecycle(model, options) && AllowedOutputModality(model, options) && AllowedProvider(model, options);
    }

    private static bool AllowedLifecycle(DiscoveredBedrockModel model, ModelDiscoveryOptions options)
    {
        if (options.IncludeLifecycleStatuses.Count == 0) return true;
        return options.IncludeLifecycleStatuses.Any(s => s.Equals(model.LifecycleStatus, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AllowedOutputModality(DiscoveredBedrockModel model, ModelDiscoveryOptions options)
    {
        if (options.IncludeOutputModalities.Count == 0) return true;
        return model.OutputModalities.Any(m => options.IncludeOutputModalities.Any(c => c.Equals(m, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool AllowedProvider(DiscoveredBedrockModel model, ModelDiscoveryOptions options)
    {
        if (options.ExcludeProviders.Any(p => p.Equals(model.ProviderName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return options.IncludeProviders.Count == 0 ||
               options.IncludeProviders.Any(p => p.Equals(model.ProviderName, StringComparison.OrdinalIgnoreCase));
    }
}

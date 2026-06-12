using System.Text.RegularExpressions;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Options;

public sealed class GatewayOptionsValidator : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.AwsRegion))
        {
            failures.Add("Gateway:AwsRegion is required.");
        }

        if (options.ModelDiscovery.CacheSeconds < 1)
        {
            failures.Add("Gateway:ModelDiscovery:CacheSeconds must be greater than zero.");
        }

        ValidateRegexPatterns(options, failures);
        ValidateLimits(options, failures);

        foreach (var model in options.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Alias))
            {
                failures.Add("Every Gateway:Models entry must define Alias.");
            }

            if (string.IsNullOrWhiteSpace(model.BedrockModelId))
            {
                failures.Add($"Gateway:Models entry '{model.Alias}' must define BedrockModelId.");
            }

            if (model.MaxInputCharacters < 1)
            {
                failures.Add($"Gateway:Models entry '{model.Alias}' must set MaxInputCharacters greater than zero.");
            }

            if (model.MaxOutputTokens < 1)
            {
                failures.Add($"Gateway:Models entry '{model.Alias}' must set MaxOutputTokens greater than zero.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateRegexPatterns(GatewayOptions options, List<string> failures)
    {
        ValidateRegexList("Gateway:BlockedPromptPatterns", options.BlockedPromptPatterns, options, failures);
        ValidateRegexList("Gateway:Policy:DeniedModelIdPatterns", options.Policy.DeniedModelIdPatterns, options, failures);
        ValidateRegexList("Gateway:Policy:AllowedModelIdPatterns", options.Policy.AllowedModelIdPatterns, options, failures);
    }

    private static void ValidateRegexList(string settingPath, IEnumerable<string> patterns, GatewayOptions options, List<string> failures)
    {
        var index = 0;
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                try
                {
                    _ = GatewayRegex.Create(pattern, options, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException ex)
                {
                    failures.Add($"{settingPath}[{index}] is not a valid regex: {ex.Message}");
                }
            }

            index++;
        }
    }

    private static void ValidateLimits(GatewayOptions options, List<string> failures)
    {
        if (options.MaxRequestBodyBytes < 1024) failures.Add("Gateway:MaxRequestBodyBytes must be at least 1024.");
        if (options.ProviderTimeoutSeconds < 1) failures.Add("Gateway:ProviderTimeoutSeconds must be greater than zero.");
        if (options.ModelDiscoveryTimeoutSeconds < 1) failures.Add("Gateway:ModelDiscoveryTimeoutSeconds must be greater than zero.");
        if (options.ReadinessTimeoutSeconds < 1) failures.Add("Gateway:ReadinessTimeoutSeconds must be greater than zero.");
        if (options.RequestLimits.MaxMetadataEntries < 0) failures.Add("Gateway:RequestLimits:MaxMetadataEntries must not be negative.");
        if (options.RequestLimits.MaxMetadataKeyLength < 1) failures.Add("Gateway:RequestLimits:MaxMetadataKeyLength must be greater than zero.");
        if (options.RequestLimits.MaxMetadataValueLength < 1) failures.Add("Gateway:RequestLimits:MaxMetadataValueLength must be greater than zero.");
        if (options.RequestLimits.MaxGatewayHeaderLength < 1) failures.Add("Gateway:RequestLimits:MaxGatewayHeaderLength must be greater than zero.");
        if (options.RequestLimits.MaxStopSequenceCount < 0) failures.Add("Gateway:RequestLimits:MaxStopSequenceCount must not be negative.");
        if (options.RequestLimits.MaxStopSequenceLength < 1) failures.Add("Gateway:RequestLimits:MaxStopSequenceLength must be greater than zero.");
        if (options.RequestLimits.MaxMessageCount < 1) failures.Add("Gateway:RequestLimits:MaxMessageCount must be greater than zero.");
        if (options.RequestLimits.RegexTimeoutMilliseconds is < 100 or > 500) failures.Add("Gateway:RequestLimits:RegexTimeoutMilliseconds must be between 100 and 500.");
    }
}

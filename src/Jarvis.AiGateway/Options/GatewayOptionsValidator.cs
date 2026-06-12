using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Options;

public sealed class GatewayOptionsValidator(IHostEnvironment? hostEnvironment = null) : IValidateOptions<GatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        var failures = new List<string>();
        var isProduction = IsProduction(options);

        if (string.IsNullOrWhiteSpace(options.AwsRegion))
        {
            failures.Add("Gateway:AwsRegion is required.");
        }

        if (isProduction && !options.RequireServiceApiKey)
        {
            failures.Add("Gateway:RequireServiceApiKey must be true in Production.");
        }

        if (options.RequireServiceApiKey)
        {
            if (string.IsNullOrWhiteSpace(options.ServiceApiKey))
            {
                failures.Add("Gateway:ServiceApiKey is required when Gateway:RequireServiceApiKey is true.");
            }
            else if (LooksLikePlaceholder(options.ServiceApiKey))
            {
                failures.Add("Gateway:ServiceApiKey must be sourced from a real secret and cannot be a placeholder.");
            }
        }

        if (options.ModelDiscovery.CacheSeconds < 1)
        {
            failures.Add("Gateway:ModelDiscovery:CacheSeconds must be greater than zero.");
        }

        if (options.RequestValidation.MinimumTemperature < 0 || options.RequestValidation.MaximumTemperature > 1 || options.RequestValidation.MinimumTemperature > options.RequestValidation.MaximumTemperature)
        {
            failures.Add("Gateway:RequestValidation temperature bounds must be within 0..1 and minimum must not exceed maximum.");
        }

        if (options.RequestValidation.MaxStopSequences < 0)
        {
            failures.Add("Gateway:RequestValidation:MaxStopSequences must be zero or greater.");
        }

        if (options.RequestValidation.MaxStopSequenceCharacters < 1)
        {
            failures.Add("Gateway:RequestValidation:MaxStopSequenceCharacters must be greater than zero.");
        }

        if (options.RequestValidation.MaxMetadataBytes < 1)
        {
            failures.Add("Gateway:RequestValidation:MaxMetadataBytes must be greater than zero.");
        }

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

    private bool IsProduction(GatewayOptions options)
    {
        return string.Equals(options.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hostEnvironment?.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikePlaceholder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim();
        return normalized.Contains("REPLACE", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("<", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("changeme", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("example", StringComparison.OrdinalIgnoreCase);
    }
}

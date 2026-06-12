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
}

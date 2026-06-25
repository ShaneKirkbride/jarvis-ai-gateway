using System.Text.RegularExpressions;
using Jarvis.AiGateway.Services;
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


        ValidateRegexes(options.BlockedPromptPatterns, "Gateway:BlockedPromptPatterns", options, failures);
        ValidateRegexes(options.Policy.AllowedModelIdPatterns, "Gateway:Policy:AllowedModelIdPatterns", options, failures);
        ValidateRegexes(options.Policy.DeniedModelIdPatterns, "Gateway:Policy:DeniedModelIdPatterns", options, failures);

        foreach (var model in options.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Alias))
            {
                failures.Add("Every Gateway:Models entry must define Alias.");
            }

            ValidateModelProviderShape(model, failures);

            if (model.MaxInputCharacters < 1)
            {
                failures.Add($"Gateway:Models entry '{model.Alias}' must set MaxInputCharacters greater than zero.");
            }

            if (model.MaxOutputTokens < 1)
            {
                failures.Add($"Gateway:Models entry '{model.Alias}' must set MaxOutputTokens greater than zero.");
            }

            ValidateModelIdentityShape(model, options.IdentityBroker.Enabled, failures);
        }

        ValidateIdentityBrokerOptions(options.IdentityBroker, failures);
        ValidateDeveloperAuthOptions(options.DeveloperAuth, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // Provider-aware identifier validation.  Each provider requires its own routing identifier, and
    // an unrecognized ProviderName fails closed — a typo must refuse startup, not silently route
    // nowhere.
    private static void ValidateModelProviderShape(ModelRouteOptions model, List<string> failures)
    {
        var provider = string.IsNullOrWhiteSpace(model.ProviderName) ? "aws-bedrock" : model.ProviderName.Trim();

        if (provider.Equals("aws-bedrock", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(model.BedrockModelId))
            {
                failures.Add($"Gateway:Models entry '{model.Alias}' must define BedrockModelId.");
            }
        }
        else if (provider.Equals("azure-openai", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(model.AzureDeploymentName))
            {
                failures.Add($"Gateway:Models entry '{model.Alias}' uses ProviderName 'azure-openai' and must define AzureDeploymentName.");
            }
        }
        else
        {
            failures.Add($"Gateway:Models entry '{model.Alias}' has an unknown ProviderName '{model.ProviderName}'. Supported providers: aws-bedrock, azure-openai.");
        }
    }

    private static void ValidateModelIdentityShape(ModelRouteOptions model, bool brokerEnabled, List<string> failures)
    {
        if (!brokerEnabled)
        {
            return;
        }

        // ITAR-approved models MUST use Entra group object IDs once the broker is active.
        // Display-name allowlists are not strong enough as an ITAR control because Entra
        // display names are mutable and not guaranteed to be unique.
        if (model.ItarApproved &&
            model.AllowedGroupIds.Count == 0 &&
            model.RequiredGroupIds.Count == 0)
        {
            failures.Add($"Gateway:Models entry '{model.Alias}' is ITAR-approved but configures no AllowedGroupIds/RequiredGroupIds. Entra group object IDs are required for ITAR authorization when the identity broker is enabled.");
        }

        // Every configured object ID must look like a real Entra GUID.  A typo here would
        // silently lock everyone out of the model, which is a security-relevant misconfig.
        foreach (var (id, source) in model.AllowedGroupIds.Select(id => (id, "AllowedGroupIds"))
            .Concat(model.RequiredGroupIds.Select(id => (id, "RequiredGroupIds"))))
        {
            if (!Guid.TryParse(id, out _))
            {
                failures.Add($"Gateway:Models entry '{model.Alias}' has a non-GUID value in {source}: '{id}'. Entra group object IDs are GUIDs.");
            }
        }
    }

    // Developer API-key auth fails closed at readiness when enabled but misconfigured: a missing
    // pepper would make every key un-hashable, and a key entry without an id/hash/owner cannot be
    // authorized safely.
    private static void ValidateDeveloperAuthOptions(DeveloperAuthOptions developerAuth, List<string> failures)
    {
        if (!developerAuth.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(developerAuth.HashPepper) || LooksLikePlaceholder(developerAuth.HashPepper))
        {
            failures.Add("Gateway:DeveloperAuth:HashPepper must be a real secret when developer auth is enabled.");
        }

        if (string.IsNullOrWhiteSpace(developerAuth.KeyPrefix))
        {
            failures.Add("Gateway:DeveloperAuth:KeyPrefix must not be empty.");
        }

        for (var i = 0; i < developerAuth.Keys.Count; i++)
        {
            var key = developerAuth.Keys[i];
            if (string.IsNullOrWhiteSpace(key.KeyId))
            {
                failures.Add($"Gateway:DeveloperAuth:Keys[{i}]:KeyId is required.");
            }
            if (string.IsNullOrWhiteSpace(key.KeyHash))
            {
                failures.Add($"Gateway:DeveloperAuth:Keys[{i}]:KeyHash is required.");
            }
            if (string.IsNullOrWhiteSpace(key.OwnerSubject) && string.IsNullOrWhiteSpace(key.OwnerEmail))
            {
                failures.Add($"Gateway:DeveloperAuth:Keys[{i}] must define OwnerSubject or OwnerEmail.");
            }
        }
    }

    private static void ValidateIdentityBrokerOptions(IdentityBrokerOptions broker, List<string> failures)
    {
        if (!broker.Enabled)
        {
            return;
        }

        if (broker.MaxAssertionAgeSeconds < 1)
        {
            failures.Add("Gateway:IdentityBroker:MaxAssertionAgeSeconds must be greater than zero.");
        }

        if (broker.ClockSkewSeconds < 0)
        {
            failures.Add("Gateway:IdentityBroker:ClockSkewSeconds must not be negative.");
        }

        if (string.IsNullOrWhiteSpace(broker.AuditSubjectSalt) || LooksLikePlaceholder(broker.AuditSubjectSalt))
        {
            failures.Add("Gateway:IdentityBroker:AuditSubjectSalt must be a real secret when the broker is enabled.");
        }

        if (broker.OwuiSessionJwt.Enabled &&
            (string.IsNullOrWhiteSpace(broker.OwuiSessionJwt.SigningKey) ||
             LooksLikePlaceholder(broker.OwuiSessionJwt.SigningKey)))
        {
            failures.Add("Gateway:IdentityBroker:OwuiSessionJwt:SigningKey must be a real secret when that validator is enabled.");
        }

        var graph = broker.Graph;
        if (string.IsNullOrWhiteSpace(graph.TenantId) || LooksLikePlaceholder(graph.TenantId))
        {
            failures.Add("Gateway:IdentityBroker:Graph:TenantId is required when the broker is enabled.");
        }
        if (string.IsNullOrWhiteSpace(graph.ClientId) || LooksLikePlaceholder(graph.ClientId))
        {
            failures.Add("Gateway:IdentityBroker:Graph:ClientId is required when the broker is enabled.");
        }
        if (string.IsNullOrWhiteSpace(graph.ClientSecret) || LooksLikePlaceholder(graph.ClientSecret))
        {
            failures.Add("Gateway:IdentityBroker:Graph:ClientSecret must be a real secret when the broker is enabled.");
        }
        if (graph.CacheSeconds < 1)
        {
            failures.Add("Gateway:IdentityBroker:Graph:CacheSeconds must be greater than zero.");
        }
        if (graph.NegativeCacheSeconds < 1)
        {
            failures.Add("Gateway:IdentityBroker:Graph:NegativeCacheSeconds must be greater than zero.");
        }
        if (graph.TimeoutSeconds < 1)
        {
            failures.Add("Gateway:IdentityBroker:Graph:TimeoutSeconds must be greater than zero.");
        }
    }

    private static void ValidateRegexes(IEnumerable<string> patterns, string path, GatewayOptions options, List<string> failures)
    {
        var index = 0;
        foreach (var pattern in patterns)
        {
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                try
                {
                    _ = GatewayRegex.Create(pattern, options);
                }
                catch (ArgumentException)
                {
                    failures.Add($"{path}[{index}] contains an invalid regex pattern.");
                }
            }

            index++;
        }
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

using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IReadinessCheck
{
    ReadinessResult Check();
}

public sealed record ReadinessResult(bool Ready, IReadOnlyList<string> FailedChecks);

public sealed class GatewayReadinessCheck(IOptions<GatewayOptions> gatewayOptions, IOptions<JwtOptions> jwtOptions, IHostEnvironment hostEnvironment) : IReadinessCheck
{
    private readonly GatewayOptions _gatewayOptions = gatewayOptions.Value;
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    public ReadinessResult Check()
    {
        var failures = new List<string>();
        var isProduction = hostEnvironment.IsProduction() || string.Equals(_gatewayOptions.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(_gatewayOptions.AwsRegion))
        {
            failures.Add("Gateway:AwsRegion is required.");
        }

        if (string.IsNullOrWhiteSpace(_jwtOptions.Authority))
        {
            failures.Add("Jwt:Authority is required.");
        }

        if (string.IsNullOrWhiteSpace(_jwtOptions.Audience))
        {
            failures.Add("Jwt:Audience is required.");
        }

        if (_gatewayOptions.RequireServiceApiKey && string.IsNullOrWhiteSpace(_gatewayOptions.ServiceApiKey))
        {
            failures.Add("Gateway:ServiceApiKey is required when service API key enforcement is enabled.");
        }

        if (_gatewayOptions.RequireServiceApiKey && GatewayOptionsValidator.LooksLikePlaceholder(_gatewayOptions.ServiceApiKey))
        {
            failures.Add("Gateway:ServiceApiKey must not be a placeholder.");
        }

        if (!_gatewayOptions.Models.Any(m => m.Enabled))
        {
            failures.Add("At least one enabled Gateway:Models alias is required.");
        }

        if (isProduction)
        {
            foreach (var model in _gatewayOptions.Models.Where(m => m.Enabled))
            {
                if (string.IsNullOrWhiteSpace(model.BedrockModelId) || model.BedrockModelId.StartsWith("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"Gateway:Models alias '{model.Alias}' must use a real Bedrock model ID in Production.");
                }
            }
        }

        return new ReadinessResult(failures.Count == 0, failures);
    }
}

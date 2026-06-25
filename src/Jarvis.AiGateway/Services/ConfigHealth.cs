using System.Text.RegularExpressions;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

/// <summary>A single configuration problem, safe to surface to operators (no secret values).</summary>
public sealed record ConfigValidationProblem(string Path, string Message);

/// <summary>
/// Captures whether the gateway's configuration is valid enough to serve traffic.  Computed once
/// at startup from the same <see cref="GatewayOptionsValidator"/> rules that previously crashed
/// the host via ValidateOnStart — but here a problem makes the gateway report NOT READY and fail
/// requests closed (503) instead of crash-looping the container.
/// </summary>
public interface IConfigHealth
{
    bool IsReady { get; }
    IReadOnlyList<ConfigValidationProblem> Problems { get; }
}

public sealed class ConfigHealth : IConfigHealth
{
    public const string InvalidCode = "GATEWAY_CONFIG_INVALID";

    // Captures a leading "Gateway:…"/"Jwt:…" config path token followed by the rest of the message.
    private static readonly Regex PathPrefix = new(@"^((?:Gateway|Jwt):[^\s]+)\s+(.*)$", RegexOptions.Compiled);

    public bool IsReady { get; }
    public IReadOnlyList<ConfigValidationProblem> Problems { get; }

    public ConfigHealth(IOptions<GatewayOptions> options, IEnumerable<IAiProvider> providers, IHostEnvironment hostEnvironment)
    {
        var problems = new List<ConfigValidationProblem>();

        // Re-run the strict config validator without crashing: every failure becomes a readiness
        // problem.  Production/ITAR/broker rules still fail closed — they just gate readiness now.
        var result = new GatewayOptionsValidator(hostEnvironment).Validate(null, options.Value);
        if (result.Failed && result.Failures is not null)
        {
            problems.AddRange(result.Failures.Select(ToProblem));
        }

        // Providers must be initialized to serve requests.
        if (!providers.Any())
        {
            problems.Add(new ConfigValidationProblem("Gateway:Providers", "No AI providers are registered."));
        }

        Problems = problems;
        IsReady = problems.Count == 0;
    }

    private static ConfigValidationProblem ToProblem(string failure)
    {
        // Defense in depth: validator messages do not contain secret values, but redact anyway.
        var safe = SecretRedactor.Redact(failure);
        var match = PathPrefix.Match(safe);
        return match.Success
            ? new ConfigValidationProblem(match.Groups[1].Value, match.Groups[2].Value)
            : new ConfigValidationProblem("Gateway", safe);
    }
}

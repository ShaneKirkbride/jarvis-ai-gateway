using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

public interface IReadinessCheck
{
    /// <summary>Synchronous config-only check used by unit tests and the legacy sync path.</summary>
    ReadinessResult Check();

    /// <summary>
    /// Full async readiness check.  Includes the synchronous config checks plus, when
    /// <c>Gateway:Readiness:CheckBedrockConnectivity=true</c>, a live ListFoundationModels
    /// call to verify VPC endpoint DNS resolution and IAM reachability.
    /// <para>
    /// IAM requirement when connectivity check is enabled:
    /// <c>bedrock:ListFoundationModels</c> on the task role.
    /// </para>
    /// </summary>
    Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed record ReadinessResult(bool Ready, IReadOnlyList<string> FailedChecks);

public sealed class GatewayReadinessCheck(
    IOptions<GatewayOptions> gatewayOptions,
    IOptions<JwtOptions> jwtOptions,
    IHostEnvironment hostEnvironment,
    IAmazonBedrock? amazonBedrock = null) : IReadinessCheck
{
    private readonly GatewayOptions _gatewayOptions = gatewayOptions.Value;
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    public ReadinessResult Check()
    {
        var failures = new List<string>();
        var isProduction = hostEnvironment.IsProduction()
            || string.Equals(_gatewayOptions.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase);
        var brokerEnabled = _gatewayOptions.IdentityBroker.Enabled;

        if (string.IsNullOrWhiteSpace(_gatewayOptions.AwsRegion))
        {
            failures.Add("Gateway:AwsRegion is required.");
        }

        // JWT bearer is only active when the identity broker is disabled.  Once the broker
        // is on, those fields are dormant and we must not block readiness on them.
        if (!brokerEnabled)
        {
            if (string.IsNullOrWhiteSpace(_jwtOptions.Authority))
            {
                failures.Add("Jwt:Authority is required.");
            }

            if (string.IsNullOrWhiteSpace(_jwtOptions.Audience))
            {
                failures.Add("Jwt:Audience is required.");
            }
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
                if (string.IsNullOrWhiteSpace(model.BedrockModelId)
                    || model.BedrockModelId.StartsWith("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"Gateway:Models alias '{model.Alias}' must use a real Bedrock model ID in Production.");
                }
            }
        }

        if (brokerEnabled)
        {
            CheckIdentityBrokerRuntimeShape(failures);
        }

        return new ReadinessResult(failures.Count == 0, failures);
    }

    /// <summary>
    /// Runtime-readiness verification of the identity-broker configuration.
    /// <para>
    /// Most of these checks are also enforced at startup by <see cref="GatewayOptionsValidator"/>
    /// with <c>ValidateOnStart()</c>, but readiness re-verifies the same invariants so a
    /// config-reload path (current or future) cannot silently weaken the gateway.
    /// </para>
    /// </summary>
    private void CheckIdentityBrokerRuntimeShape(List<string> failures)
    {
        var broker = _gatewayOptions.IdentityBroker;

        if (string.IsNullOrWhiteSpace(broker.AuditSubjectSalt) || GatewayOptionsValidator.LooksLikePlaceholder(broker.AuditSubjectSalt))
        {
            failures.Add("Gateway:IdentityBroker:AuditSubjectSalt must be a real secret.");
        }

        if (broker.OwuiSessionJwt.Enabled &&
            (string.IsNullOrWhiteSpace(broker.OwuiSessionJwt.SigningKey) ||
             GatewayOptionsValidator.LooksLikePlaceholder(broker.OwuiSessionJwt.SigningKey)))
        {
            failures.Add("Gateway:IdentityBroker:OwuiSessionJwt:SigningKey must be a real secret when the OWUI validator is enabled.");
        }

        var graph = broker.Graph;
        if (string.IsNullOrWhiteSpace(graph.TenantId) || GatewayOptionsValidator.LooksLikePlaceholder(graph.TenantId))
        {
            failures.Add("Gateway:IdentityBroker:Graph:TenantId is required.");
        }
        if (string.IsNullOrWhiteSpace(graph.ClientId) || GatewayOptionsValidator.LooksLikePlaceholder(graph.ClientId))
        {
            failures.Add("Gateway:IdentityBroker:Graph:ClientId is required.");
        }
        if (string.IsNullOrWhiteSpace(graph.ClientSecret) || GatewayOptionsValidator.LooksLikePlaceholder(graph.ClientSecret))
        {
            failures.Add("Gateway:IdentityBroker:Graph:ClientSecret must be a real secret.");
        }

        // ITAR-approved models must NOT be authorized solely by display names once the
        // broker is enabled.  This is the operational tripwire the plan calls for.
        foreach (var model in _gatewayOptions.Models.Where(m => m.Enabled && m.ItarApproved))
        {
            if (model.AllowedGroupIds.Count == 0 && model.RequiredGroupIds.Count == 0)
            {
                failures.Add($"Gateway:Models alias '{model.Alias}' is ITAR-approved but configures no AllowedGroupIds/RequiredGroupIds. Display-name groups are insufficient for ITAR authorization with the identity broker enabled.");
            }
        }

        // The trusted-header validator has no token signature — its trust depends entirely
        // on the mTLS subject being pinned.  Refuse to start if the operator enabled the
        // trusted-header path without also enabling subject pinning.
        if (broker.OwuiTrustedHeader.Enabled &&
            broker.OwuiTrustedHeader.RequireMtlsSubjectPinning &&
            !broker.Mtls.RequireSubjectCheck)
        {
            failures.Add("Gateway:IdentityBroker:OwuiTrustedHeader is enabled but Mtls.RequireSubjectCheck is false. The trusted-header validator must be paired with mTLS subject pinning so the trust chain is anchored to a pinned client certificate.");
        }

        if (broker.Mtls.RequireSubjectCheck &&
            broker.Mtls.AcceptedClientCertSubjects.Count == 0 &&
            broker.Mtls.AcceptedClientCertSerials.Count == 0)
        {
            failures.Add("Gateway:IdentityBroker:Mtls:RequireSubjectCheck is true but no AcceptedClientCertSubjects or AcceptedClientCertSerials are configured. Configure at least one pinned subject or serial.");
        }
    }

    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var configResult = Check();
        if (!_gatewayOptions.Readiness.CheckBedrockConnectivity || amazonBedrock is null)
        {
            return configResult;
        }

        var failures = configResult.FailedChecks.ToList();
        await CheckBedrockConnectivityAsync(failures, cancellationToken);
        return new ReadinessResult(failures.Count == 0, failures);
    }

    private async Task CheckBedrockConnectivityAsync(List<string> failures, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _gatewayOptions.ReadinessTimeoutSeconds)));

        try
        {
            // A single-item page is enough to confirm connectivity + IAM.
            // amazonBedrock is guaranteed non-null here — CheckAsync only calls this method when it is not null.
            await amazonBedrock!.ListFoundationModelsAsync(new ListFoundationModelsRequest(), timeoutCts.Token);
        }
        catch (Amazon.Bedrock.Model.AccessDeniedException)
        {
            // The task role may have InvokeModel but not ListFoundationModels.
            // Connectivity is likely fine; flag the missing permission explicitly.
            failures.Add("Bedrock connectivity check: bedrock:ListFoundationModels was denied. " +
                         "Either grant the permission or disable Gateway:Readiness:CheckBedrockConnectivity.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            failures.Add($"Bedrock connectivity check timed out after {_gatewayOptions.ReadinessTimeoutSeconds}s. " +
                         "Verify the VPC endpoint DNS name and security group rules.");
        }
        catch (Exception ex)
        {
            failures.Add($"Bedrock connectivity check failed: {ex.GetType().Name}. " +
                         "Verify Gateway:AwsRegion, VPC endpoint configuration, and network reachability.");
        }
    }
}

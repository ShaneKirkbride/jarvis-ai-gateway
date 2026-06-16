using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Hosting;
using Moq;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// Verifies that /readyz fails closed at runtime if any broker-required secret or shape is
/// missing.  These are belt-and-suspenders re-checks of what
/// <see cref="GatewayOptionsValidator"/> enforces at startup — readiness re-validates the
/// same invariants every probe so a config-reload path cannot silently weaken the gateway.
/// </summary>
public sealed class IdentityBrokerReadinessTests
{
    [Fact]
    public void Broker_enabled_without_audit_salt_fails_readiness()
    {
        var options = NewBrokerOptions();
        options.IdentityBroker.AuditSubjectSalt = null;

        var result = NewCheck(options).Check();

        Assert.False(result.Ready);
        Assert.Contains(result.FailedChecks, f => f.Contains("AuditSubjectSalt"));
    }

    [Fact]
    public void Broker_enabled_without_owui_signing_key_fails_readiness()
    {
        var options = NewBrokerOptions();
        options.IdentityBroker.OwuiSessionJwt.SigningKey = null;

        var result = NewCheck(options).Check();

        Assert.False(result.Ready);
        Assert.Contains(result.FailedChecks, f => f.Contains("SigningKey"));
    }

    [Fact]
    public void Broker_enabled_without_graph_credentials_fails_readiness()
    {
        var options = NewBrokerOptions();
        options.IdentityBroker.Graph.ClientSecret = null;

        var result = NewCheck(options).Check();

        Assert.False(result.Ready);
        Assert.Contains(result.FailedChecks, f => f.Contains("ClientSecret"));
    }

    [Fact]
    public void Broker_enabled_with_itar_model_lacking_group_ids_fails_readiness()
    {
        var options = NewBrokerOptions();
        options.Models =
        [
            new ModelRouteOptions
            {
                Alias = "itar-bad",
                BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0",
                Enabled = true,
                ItarApproved = true,
                AllowedGroups = ["ITAR-Approved"]   // display-name only — insufficient
            }
        ];

        var result = NewCheck(options).Check();

        Assert.False(result.Ready);
        Assert.Contains(result.FailedChecks, f => f.Contains("itar-bad") && f.Contains("ITAR"));
    }

    [Fact]
    public void Broker_enabled_skips_jwt_authority_and_audience_checks()
    {
        // Jwt fields are intentionally empty — they should not block readiness when the
        // broker is on.
        var options = NewBrokerOptions();
        var check = new GatewayReadinessCheck(
            MsOptions.Create(options),
            MsOptions.Create(new JwtOptions()),
            ProductionEnv());

        var result = check.Check();

        Assert.True(result.Ready);
    }

    [Fact]
    public void Broker_disabled_path_still_requires_jwt_authority()
    {
        var options = NewBrokerOptions();
        options.IdentityBroker.Enabled = false;

        var check = new GatewayReadinessCheck(
            MsOptions.Create(options),
            MsOptions.Create(new JwtOptions()),   // missing authority + audience
            ProductionEnv());

        var result = check.Check();

        Assert.False(result.Ready);
        Assert.Contains(result.FailedChecks, f => f.Contains("Jwt:Authority"));
    }

    private static GatewayReadinessCheck NewCheck(GatewayOptions options) => new(
        MsOptions.Create(options),
        MsOptions.Create(new JwtOptions { Authority = "https://issuer.example", Audience = "api" }),
        ProductionEnv());

    private static IHostEnvironment ProductionEnv()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Production");
        return env.Object;
    }

    private static GatewayOptions NewBrokerOptions() => new()
    {
        AwsRegion = "us-gov-west-1",
        EnvironmentName = "Production",
        RequireServiceApiKey = true,
        ServiceApiKey = "real-secret-value",
        IdentityBroker = new IdentityBrokerOptions
        {
            Enabled = true,
            AuditSubjectSalt = "real-salt-value",
            OwuiSessionJwt = new OwuiSessionJwtOptions
            {
                Enabled = true,
                SigningKey = "real-signing-key-value-padding-padding"
            },
            Graph = new GraphOptions
            {
                TenantId = "real-tenant-guid",
                ClientId = "real-client-guid",
                ClientSecret = "real-client-secret-padding"
            },
            // PR 4 flipped Mtls.RequireSubjectCheck default to true; the readiness check
            // refuses to start a broker-enabled deploy without a pinned subject or serial.
            Mtls = new MtlsOptions
            {
                RequireSubjectCheck = true,
                AcceptedClientCertSubjects = ["CN=jarvis-openwebui"]
            }
        },
        Models =
        [
            new ModelRouteOptions
            {
                Alias = "general",
                BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0",
                Enabled = true
            }
        ]
    };
}

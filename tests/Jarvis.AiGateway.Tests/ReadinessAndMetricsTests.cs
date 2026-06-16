using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.Hosting;
using Moq;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class ReadinessAndMetricsTests
{
    [Fact]
    public void Readiness_check_reports_ready_for_valid_non_secret_configuration()
    {
        var result = new GatewayReadinessCheck(MsOptions.Create(Options()), MsOptions.Create(new JwtOptions { Authority = "https://issuer.example", Audience = "api" }), Environment("Production")).Check();

        Assert.True(result.Ready);
        Assert.Empty(result.FailedChecks);
    }

    [Fact]
    public void Readiness_check_reports_failed_non_secret_configuration_without_leaking_values()
    {
        var options = Options();
        options.AwsRegion = string.Empty;
        options.ServiceApiKey = "REPLACE_WITH_SECRET";
        options.Models[0].Enabled = false;
        options.Models.Add(new ModelRouteOptions { Alias = "placeholder", BedrockModelId = "REPLACE_WITH_MODEL", Enabled = true });
        var result = new GatewayReadinessCheck(MsOptions.Create(options), MsOptions.Create(new JwtOptions()), Environment("Production")).Check();

        Assert.False(result.Ready);
        Assert.Contains(result.FailedChecks, f => f.Contains("AwsRegion"));
        Assert.Contains(result.FailedChecks, f => f.Contains("Jwt:Authority"));
        Assert.Contains(result.FailedChecks, f => f.Contains("Jwt:Audience"));
        Assert.Contains(result.FailedChecks, f => f.Contains("placeholder"));
        Assert.DoesNotContain(result.FailedChecks, f => f.Contains("REPLACE_WITH_SECRET"));
    }


    [Fact]
    public void Readiness_check_reports_missing_service_key_and_missing_enabled_aliases()
    {
        var options = Options();
        options.ServiceApiKey = string.Empty;
        options.Models[0].Enabled = false;

        var result = new GatewayReadinessCheck(MsOptions.Create(options), MsOptions.Create(new JwtOptions { Authority = "https://issuer.example", Audience = "api" }), Environment("Production")).Check();

        Assert.False(result.Ready);
        Assert.Contains(result.FailedChecks, f => f.Contains("ServiceApiKey"));
        Assert.Contains(result.FailedChecks, f => f.Contains("At least one enabled"));
    }

    [Fact]
    public async Task Readiness_check_async_returns_ready_when_connectivity_check_disabled()
    {
        var result = await new GatewayReadinessCheck(
            MsOptions.Create(Options()),
            MsOptions.Create(new JwtOptions { Authority = "https://issuer.example", Audience = "api" }),
            Environment("Production")).CheckAsync();

        Assert.True(result.Ready);
        Assert.Empty(result.FailedChecks);
    }

    [Fact]
    public async Task Readiness_check_async_adds_failure_when_bedrock_connectivity_check_fails()
    {
        var options = Options();
        options.Readiness = new ReadinessOptions { CheckBedrockConnectivity = true };
        var bedrockMock = new Mock<IAmazonBedrock>();
        bedrockMock
            .Setup(b => b.ListFoundationModelsAsync(It.IsAny<ListFoundationModelsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mock DNS failure"));

        var result = await new GatewayReadinessCheck(
            MsOptions.Create(options),
            MsOptions.Create(new JwtOptions { Authority = "https://issuer.example", Audience = "api" }),
            Environment("Production"),
            bedrockMock.Object).CheckAsync();

        Assert.False(result.Ready);
        Assert.Contains(result.FailedChecks, f => f.Contains("Bedrock connectivity check failed"));
    }

    [Fact]
    public async Task Readiness_check_async_flags_access_denied_with_actionable_message()
    {
        var options = Options();
        options.Readiness = new ReadinessOptions { CheckBedrockConnectivity = true };
        var bedrockMock = new Mock<IAmazonBedrock>();
        bedrockMock
            .Setup(b => b.ListFoundationModelsAsync(It.IsAny<ListFoundationModelsRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Amazon.Bedrock.Model.AccessDeniedException("Access denied"));

        var result = await new GatewayReadinessCheck(
            MsOptions.Create(options),
            MsOptions.Create(new JwtOptions { Authority = "https://issuer.example", Audience = "api" }),
            Environment("Production"),
            bedrockMock.Object).CheckAsync();

        Assert.False(result.Ready);
        Assert.Contains(result.FailedChecks, f => f.Contains("bedrock:ListFoundationModels was denied"));
    }

    [Fact]
    public void Gateway_metrics_methods_are_safe_noops_without_exporter()
    {
        var metrics = new GatewayMetrics();

        metrics.RecordRequest("model");
        metrics.RecordLatency("model", TimeSpan.FromMilliseconds(1));
        metrics.RecordPolicyDenial("RULE", "model");
        metrics.RecordBedrockInvocation("strategy", TimeSpan.FromMilliseconds(2), success: true);
        metrics.RecordBedrockError("model");
        metrics.RecordServerError("route");
        metrics.RecordTokenUsage("model", 1, 2);
    }

    private static GatewayOptions Options() => new()
    {
        EnvironmentName = "Production",
        RequireServiceApiKey = true,
        ServiceApiKey = "real-production-secret",
        // These tests exercise the legacy JwtBearer readiness path; opt the broker out
        // explicitly so the suite continues to validate that behaviour after the broker
        // default was flipped to on.
        IdentityBroker = new IdentityBrokerOptions { Enabled = false },
        Models = [new ModelRouteOptions { Alias = "alias", BedrockModelId = "anthropic.claude-example-v1", Enabled = true }]
    };

    private static IHostEnvironment Environment(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(name);
        return environment.Object;
    }
}

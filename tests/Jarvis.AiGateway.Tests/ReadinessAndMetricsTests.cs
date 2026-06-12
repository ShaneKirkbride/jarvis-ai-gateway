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
        Models = [new ModelRouteOptions { Alias = "alias", BedrockModelId = "anthropic.claude-example-v1", Enabled = true }]
    };

    private static IHostEnvironment Environment(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(name);
        return environment.Object;
    }
}

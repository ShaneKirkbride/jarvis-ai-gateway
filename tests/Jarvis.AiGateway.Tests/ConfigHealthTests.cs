using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

public sealed class ConfigHealthTests
{
    [Fact]
    public void Valid_configuration_is_ready()
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions { Enabled = false },
            Models = [new ModelRouteOptions { Alias = "general", BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0" }]
        };

        var health = new ConfigHealth(MsOptions.Create(options), [new FakeProvider()], Env("Development"));

        Assert.True(health.IsReady);
        Assert.Empty(health.Problems);
    }

    [Fact]
    public void Production_without_service_key_is_not_ready_and_reports_path()
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions { Enabled = false },
            RequireServiceApiKey = false,
            Models = [new ModelRouteOptions { Alias = "general", BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0" }]
        };

        var health = new ConfigHealth(MsOptions.Create(options), [new FakeProvider()], Env("Production"));

        Assert.False(health.IsReady);
        var problem = Assert.Single(health.Problems, p => p.Path.Contains("RequireServiceApiKey"));
        Assert.Contains("Production", problem.Message);
    }

    [Fact]
    public void Enabled_broker_without_graph_secrets_is_not_ready()
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions { Enabled = true, AuditSubjectSalt = null },
            Models = [new ModelRouteOptions { Alias = "general", BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0" }]
        };

        var health = new ConfigHealth(MsOptions.Create(options), [new FakeProvider()], Env("Development"));

        Assert.False(health.IsReady);
        Assert.Contains(health.Problems, p => p.Path.Contains("Graph:TenantId"));
        Assert.Contains(health.Problems, p => p.Path.Contains("Graph:ClientId"));
        Assert.Contains(health.Problems, p => p.Path.Contains("AuditSubjectSalt"));
    }

    [Fact]
    public void No_providers_is_not_ready()
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions { Enabled = false },
            Models = [new ModelRouteOptions { Alias = "general", BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0" }]
        };

        var health = new ConfigHealth(MsOptions.Create(options), [], Env("Development"));

        Assert.False(health.IsReady);
        Assert.Contains(health.Problems, p => p.Path == "Gateway:Providers");
    }

    [Fact]
    public void Failure_without_path_prefix_falls_back_to_gateway_path()
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions { Enabled = false },
            // Blank alias produces "Every Gateway:Models entry must define Alias." (no leading path token).
            Models = [new ModelRouteOptions { Alias = " ", BedrockModelId = "x" }]
        };

        var health = new ConfigHealth(MsOptions.Create(options), [new FakeProvider()], Env("Development"));

        Assert.False(health.IsReady);
        Assert.Contains(health.Problems, p => p.Path == "Gateway" && p.Message.Contains("Alias"));
    }

    [Fact]
    public void Diagnostics_do_not_leak_secret_values()
    {
        var options = new GatewayOptions
        {
            IdentityBroker = new IdentityBrokerOptions { Enabled = false },
            RequireServiceApiKey = true,
            ServiceApiKey = "REPLACE_WITH_SECRET",
            Models = [new ModelRouteOptions { Alias = "general", BedrockModelId = "anthropic.claude-3-haiku-20240307-v1:0" }]
        };

        var health = new ConfigHealth(MsOptions.Create(options), [new FakeProvider()], Env("Development"));

        Assert.False(health.IsReady);
        Assert.All(health.Problems, p => Assert.DoesNotContain("REPLACE_WITH_SECRET", $"{p.Path} {p.Message}"));
    }

    private static IHostEnvironment Env(string name) => new FakeEnv(name);

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeProvider : IAiProvider
    {
        public string ProviderName => "aws-bedrock";
        public Task<AiChatResult> CompleteAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiChatResult("x", new TokenUsage(0, 0, 0), "stop", new ProviderInvocationMetadata("aws-bedrock", "x", 0)));
        public IAsyncEnumerable<AiChatStreamEvent> StreamAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

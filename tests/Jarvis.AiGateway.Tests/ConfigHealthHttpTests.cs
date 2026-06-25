using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// Proves the operational failure mode: a misconfigured gateway does NOT crash-loop. The host
/// boots, /healthz/live stays 200, /healthz/ready reports a safe 503, and protected /v1 routes
/// fail closed with 503 — while a valid (production) configuration is fully ready.
/// </summary>
public sealed class ConfigHealthHttpTests :
    IClassFixture<ConfigHealthHttpTests.ValidProductionFactory>,
    IClassFixture<ConfigHealthHttpTests.InvalidProductionFactory>
{
    private readonly ValidProductionFactory _valid;
    private readonly InvalidProductionFactory _invalid;

    public ConfigHealthHttpTests(ValidProductionFactory valid, InvalidProductionFactory invalid)
    {
        _valid = valid;
        _invalid = invalid;
    }

    [Fact]
    public async Task Live_returns_200_even_when_config_invalid()
    {
        // The very fact this responds proves the host booted instead of crash-looping.
        var response = await _invalid.CreateClient().GetAsync("/healthz/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_returns_503_with_config_invalid_code_and_redacted_errors()
    {
        var response = await _invalid.CreateClient().GetAsync("/healthz/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("unhealthy", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(ConfigHealth.InvalidCode, doc.RootElement.GetProperty("code").GetString());
        var errors = doc.RootElement.GetProperty("errors").EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.All(errors, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("path").GetString()));
            Assert.DoesNotContain("REPLACE_WITH", e.GetProperty("message").GetString());
        });
        Assert.Contains(errors, e => e.GetProperty("path").GetString()!.Contains("RequireServiceApiKey"));
    }

    [Fact]
    public async Task Protected_route_returns_503_config_invalid_when_not_ready()
    {
        var response = await _invalid.CreateClient().PostAsync("/v1/chat/completions",
            new StringContent("""{"model":"general","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ConfigHealth.InvalidCode, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("service_unavailable", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Valid_production_config_is_ready()
    {
        var live = await _valid.CreateClient().GetAsync("/healthz/live");
        var ready = await _valid.CreateClient().GetAsync("/healthz/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var doc = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
    }

    private static Dictionary<string, string?> BaseConfig() => new()
    {
        ["Gateway:IdentityBroker:Enabled"] = "false",
        ["Gateway:ModelDiscovery:Enabled"] = "false",
        ["Gateway:Models:0:Alias"] = "general",
        ["Gateway:Models:0:BedrockModelId"] = "anthropic.claude-3-haiku-20240307-v1:0",
        ["Gateway:Models:0:RequiredGroups:0"] = "AI-General-Users"
    };

    public sealed class ValidProductionFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                TestConfig.RemoveJsonSources(config);
                var settings = BaseConfig();
                settings["Jwt:Authority"] = "https://issuer.example.test/";
                settings["Jwt:Audience"] = "jarvis";
                settings["Gateway:RequireServiceApiKey"] = "true";
                settings["Gateway:ServiceApiKey"] = "a-real-service-key-value";
                config.AddInMemoryCollection(settings);
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGraphGroupQueryExecutor>();
                services.AddSingleton<IGraphGroupQueryExecutor, FakeGraphGroupQueryExecutor>();
            });
        }
    }

    public sealed class InvalidProductionFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                TestConfig.RemoveJsonSources(config);
                // Production but RequireServiceApiKey is left false → invalid, fail-closed (not crash).
                config.AddInMemoryCollection(BaseConfig());
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGraphGroupQueryExecutor>();
                services.AddSingleton<IGraphGroupQueryExecutor, FakeGraphGroupQueryExecutor>();
            });
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Jarvis.AiGateway.Models;
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
/// End-to-end coverage for the machine-to-machine (Open WebUI → Envoy → gateway) auth path.
/// Unlike <see cref="OpenWebUiContractTests"/>, this factory deliberately does NOT replace the
/// authentication scheme provider, so the real JwtBearer handler is registered.  A token-less
/// service call therefore reproduces production exactly: JwtBearer returns NoResult and only the
/// service-key principal established by ServiceApiKeyMiddleware can satisfy RequireAuthorization().
/// This is the regression guard for the "AuthenticationScheme: Bearer was challenged" 401.
/// </summary>
public sealed class ServiceApiKeyAuthIntegrationTests : IClassFixture<ServiceApiKeyAuthIntegrationTests.ServiceKeyFactory>
{
    private const string ServiceKey = "test-service-key-do-not-use-in-prod";

    private readonly ServiceKeyFactory _factory;

    public ServiceApiKeyAuthIntegrationTests(ServiceKeyFactory factory) => _factory = factory;

    [Fact]
    public async Task Valid_service_key_without_user_token_can_access_models()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Gateway-Key", ServiceKey);

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Missing_service_key_is_rejected_when_required()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_service_key_is_rejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Gateway-Key", "wrong-key");

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_remains_open_without_any_auth()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public sealed class ServiceKeyFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // JwtBearer is registered (broker off) but never contacts the authority:
                    // these requests carry no token, so the handler short-circuits to NoResult.
                    ["Jwt:Authority"] = "https://issuer.example.test/",
                    ["Jwt:Audience"] = "jarvis",
                    ["Jwt:RequireHttpsMetadata"] = "false",
                    ["Gateway:RequireServiceApiKey"] = "true",
                    ["Gateway:ServiceApiKey"] = ServiceKey,
                    ["Gateway:IdentityBroker:Enabled"] = "false",
                    ["Gateway:ModelDiscovery:Enabled"] = "false",
                    // A model with no required groups is visible to the groupless service
                    // principal, so /v1/models returns a populated list under the service key.
                    ["Gateway:Models:0:Alias"] = "general",
                    ["Gateway:Models:0:BedrockModelId"] = "anthropic.claude-3-haiku-20240307-v1:0",
                    ["Gateway:Models:0:SupportsConverse"] = "true"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                // The broker DI graph is registered because Program.cs captures the (default-on)
                // broker flag before this deferred config merges; swap the real Graph adapter for
                // a fake so startup does not depend on Entra credentials.  The broker middleware
                // still short-circuits at runtime because the merged config sets Enabled=false.
                services.RemoveAll<IGraphGroupQueryExecutor>();
                services.AddSingleton<IGraphGroupQueryExecutor, FakeGraphGroupQueryExecutor>();
            });
        }
    }
}

/// <summary>
/// Verifies that <see cref="GatewayOptions.ServiceApiKeyGroups"/> drives group-based policy
/// for the M2M service principal.  Models with explicit <c>RequiredGroups</c> are visible
/// and accessible only when the service principal carries a matching group claim; models
/// requiring groups the principal does not have remain denied.
/// </summary>
public sealed class ServiceApiKeyGroupPolicyTests : IClassFixture<ServiceApiKeyGroupPolicyTests.ServiceKeyGroupFactory>
{
    private const string ServiceKey = "test-service-key-do-not-use-in-prod";

    private readonly ServiceKeyGroupFactory _factory;

    public ServiceApiKeyGroupPolicyTests(ServiceKeyGroupFactory factory) => _factory = factory;

    [Fact]
    public async Task Service_key_principal_can_list_itar_approved_model()
    {
        var client = ServiceKeyClient();

        var response = await client.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains(doc.RootElement.GetProperty("data").EnumerateArray(),
            e => e.GetProperty("id").GetString() == "itar-model");
    }

    [Fact]
    public async Task Service_key_principal_can_list_general_model_when_group_rules_match()
    {
        var client = ServiceKeyClient();

        var response = await client.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains(doc.RootElement.GetProperty("data").EnumerateArray(),
            e => e.GetProperty("id").GetString() == "general-model");
    }

    [Fact]
    public async Task Service_key_principal_cannot_list_model_requiring_group_it_does_not_have()
    {
        var client = ServiceKeyClient();

        var response = await client.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.DoesNotContain(doc.RootElement.GetProperty("data").EnumerateArray(),
            e => e.GetProperty("id").GetString() == "evaluators-only");
    }

    [Fact]
    public async Task Service_key_principal_gets_403_for_model_requiring_group_it_does_not_have()
    {
        var client = ServiceKeyClient();
        var body = new StringContent(
            JsonSerializer.Serialize(new { model = "evaluators-only", messages = new[] { new { role = "user", content = "hello" } } }),
            Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/v1/chat/completions", body);
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("USER_GROUP_DENIED", responseBody);
    }

    [Fact]
    public async Task Missing_service_key_is_rejected_even_with_group_policy_configured()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private System.Net.Http.HttpClient ServiceKeyClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Gateway-Key", ServiceKey);
        return client;
    }

    public sealed class ServiceKeyGroupFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Authority"] = "https://issuer.example.test/",
                    ["Jwt:Audience"] = "jarvis",
                    ["Jwt:RequireHttpsMetadata"] = "false",
                    ["Gateway:RequireServiceApiKey"] = "true",
                    ["Gateway:ServiceApiKey"] = ServiceKey,
                    ["Gateway:IdentityBroker:Enabled"] = "false",
                    ["Gateway:ModelDiscovery:Enabled"] = "false",
                    // Service principal is granted these two groups — matches itar-model and
                    // general-model but NOT evaluators-only, so policy denies that model.
                    ["Gateway:ServiceApiKeyGroups:0"] = "AI-ITAR-Approved",
                    ["Gateway:ServiceApiKeyGroups:1"] = "AI-General-Users",
                    // ITAR-approved model: only AI-ITAR-Approved may access it.
                    ["Gateway:Models:0:Alias"] = "itar-model",
                    ["Gateway:Models:0:BedrockModelId"] = "anthropic.claude-3-haiku-20240307-v1:0",
                    ["Gateway:Models:0:SupportsConverse"] = "true",
                    ["Gateway:Models:0:RequiredGroups:0"] = "AI-ITAR-Approved",
                    // General model: any of the two groups qualifies.
                    ["Gateway:Models:1:Alias"] = "general-model",
                    ["Gateway:Models:1:BedrockModelId"] = "anthropic.claude-3-haiku-20240307-v1:0",
                    ["Gateway:Models:1:SupportsConverse"] = "true",
                    ["Gateway:Models:1:RequiredGroups:0"] = "AI-General-Users",
                    ["Gateway:Models:1:RequiredGroups:1"] = "AI-ITAR-Approved",
                    // Restricted model: requires a group the service principal does not have.
                    ["Gateway:Models:2:Alias"] = "evaluators-only",
                    ["Gateway:Models:2:BedrockModelId"] = "anthropic.claude-3-haiku-20240307-v1:0",
                    ["Gateway:Models:2:SupportsConverse"] = "true",
                    ["Gateway:Models:2:RequiredGroups:0"] = "AI-Jarvis-Evaluators"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGraphGroupQueryExecutor>();
                services.AddSingleton<IGraphGroupQueryExecutor, FakeGraphGroupQueryExecutor>();
                services.RemoveAll<IBedrockInvocationStrategy>();
                services.AddSingleton<IBedrockInvocationStrategy, AllowAllFakeBedrockStrategy>();
                services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
            });
        }
    }

    private sealed class AllowAllFakeBedrockStrategy : IBedrockInvocationStrategy
    {
        public string Name => "fake-all";
        public bool CanHandle(GatewayModel model, AiChatRequest request) => true;
        public Task<AiChatResult> InvokeAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiChatResult("ok", new TokenUsage(1, 1, 2), "stop", new ProviderInvocationMetadata("fake", Name, 1, "req-id")));
    }

    private sealed class NoOpAuditLogger : IAuditLogger
    {
        public void Write(GatewayAuditEvent auditEvent) { }
        public void WriteIdentity(IdentityAuditEvent auditEvent) { }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// End-to-end coverage through the full middleware + endpoint pipeline for a provider-neutral
/// configured deployment.  Reproduces the reported Azure Container Apps scenario: a configured
/// azure-openai alias must surface in /v1/models with the correct owned_by and complete a chat
/// request by routing to AzureOpenAiProvider — not be rejected as a Bedrock placeholder.
/// </summary>
public sealed class AzureOpenAiEndToEndTests : IClassFixture<AzureOpenAiEndToEndTests.AzureGatewayFactory>
{
    private readonly AzureGatewayFactory _factory;

    public AzureOpenAiEndToEndTests(AzureGatewayFactory factory) => _factory = factory;

    [Fact]
    public async Task Models_lists_azure_alias_with_azure_owned_by()
    {
        var response = await AuthedClient().GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var models = doc.RootElement.GetProperty("data").EnumerateArray()
            .ToDictionary(m => m.GetProperty("id").GetString()!, m => m.GetProperty("owned_by").GetString());

        Assert.True(models.ContainsKey("jarvis2-chat"));
        Assert.Equal("azure-openai", models["jarvis2-chat"]);
        // Bedrock alias is unchanged.
        Assert.Equal("aws-bedrock", models["general"]);
    }

    [Fact]
    public async Task Azure_model_completes_and_routes_to_azure_provider()
    {
        var response = await AuthedClient().PostAsync("/v1/chat/completions", Json(new
        {
            model = "jarvis2-chat",
            messages = new[] { new { role = "user", content = "hello" } }
        }));
        var body = await response.Content.ReadAsStringAsync();

        // The pre-fix bug returned 403 MODEL_PLACEHOLDER_ID here.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("MODEL_PLACEHOLDER_ID", body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("from-azure-openai", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        // Proves the request reached AzureOpenAiProvider over HTTP at the configured deployment URL.
        Assert.Equal("/openai/deployments/jarvis2-chat/chat/completions", AzureGatewayFactory.LastAzurePath);
    }

    [Fact]
    public async Task Bedrock_model_still_completes_via_bedrock()
    {
        var response = await AuthedClient().PostAsync("/v1/chat/completions", Json(new
        {
            model = "general",
            messages = new[] { new { role = "user", content = "hello" } }
        }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("from-bedrock", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    private System.Net.Http.HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");
        return client;
    }

    private static StringContent Json<T>(T value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public sealed class AzureGatewayFactory : WebApplicationFactory<Program>
    {
        public static volatile string? LastAzurePath;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                // Own the full Gateway:Models / AzureOpenAi config; do not inherit appsettings models.
                TestConfig.RemoveJsonSources(config);
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Authority"] = "https://issuer.example.test/",
                    ["Jwt:Audience"] = "jarvis",
                    ["Jwt:RequireHttpsMetadata"] = "false",
                    ["Gateway:IdentityBroker:Enabled"] = "false",
                    ["Gateway:ModelDiscovery:Enabled"] = "false",
                    ["Gateway:AzureOpenAi:Endpoint"] = "https://test-jarvis.openai.azure.us/",
                    ["Gateway:AzureOpenAi:ApiVersion"] = "2025-04-01-preview",
                    ["Gateway:AzureOpenAi:AuthenticationMode"] = "ApiKey",
                    ["Gateway:AzureOpenAi:ApiKey"] = "test-key",
                    ["Gateway:Models:0:Alias"] = "general",
                    ["Gateway:Models:0:BedrockModelId"] = "anthropic.claude-3-haiku-20240307-v1:0",
                    ["Gateway:Models:0:RequiredGroups:0"] = "AI-General-Users",
                    ["Gateway:Models:0:SupportsConverse"] = "true",
                    ["Gateway:Models:1:Alias"] = "jarvis2-chat",
                    ["Gateway:Models:1:ProviderName"] = "azure-openai",
                    ["Gateway:Models:1:AzureDeploymentName"] = "jarvis2-chat",
                    ["Gateway:Models:1:RequiredGroups:0"] = "AI-General-Users"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticationSchemeProvider>();
                services.AddSingleton<IAuthenticationSchemeProvider, AzureSchemeProvider>();
                services.RemoveAll<IBedrockInvocationStrategy>();
                services.AddSingleton<IBedrockInvocationStrategy, FakeBedrockStrategy>();
                services.RemoveAll<IBedrockStreamingStrategy>();
                services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
                services.RemoveAll<IGraphGroupQueryExecutor>();
                services.AddSingleton<IGraphGroupQueryExecutor, FakeGraphGroupQueryExecutor>();
                // Stub the Azure data-plane HTTP call so no real Azure endpoint is contacted.
                services.AddHttpClient(AzureOpenAiProvider.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubAzureHandler());
            });
        }

        private sealed class StubAzureHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastAzurePath = request.RequestUri?.AbsolutePath;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"choices":[{"message":{"content":"from-azure-openai"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}""",
                        Encoding.UTF8,
                        "application/json")
                });
            }
        }

        private sealed class FakeBedrockStrategy : IBedrockInvocationStrategy
        {
            public string Name => "fake-bedrock";
            public bool CanHandle(GatewayModel model, AiChatRequest request) => model.ProviderName == "aws-bedrock";
            public Task<AiChatResult> InvokeAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
                Task.FromResult(new AiChatResult("from-bedrock", new TokenUsage(1, 1, 2), "stop", new ProviderInvocationMetadata("aws-bedrock", Name, 1)));
        }

        private sealed class NoOpAuditLogger : IAuditLogger
        {
            public void Write(GatewayAuditEvent auditEvent) { }
            public void WriteIdentity(IdentityAuditEvent auditEvent) { }
        }

        private sealed class AzureSchemeProvider : AuthenticationSchemeProvider
        {
            private static readonly AuthenticationScheme TestScheme = new("Bearer", "Bearer", typeof(AzureAuthHandler));

            public AzureSchemeProvider(IOptions<AuthenticationOptions> options) : base(options) { }

            public override Task<AuthenticationScheme?> GetSchemeAsync(string name) =>
                Task.FromResult<AuthenticationScheme?>(name == "Bearer" ? TestScheme : null);

            public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);

            public override Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);

            public override Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() =>
                Task.FromResult<IEnumerable<AuthenticationScheme>>([TestScheme]);
        }

        private sealed class AzureAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var claims = new[]
                {
                    new Claim("sub", "azure-user"),
                    new Claim("email", "azure-user@example.test"),
                    new Claim("groups", "AI-General-Users")
                };
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
            }
        }
    }
}

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

/// <summary>End-to-end coverage of /v1/embeddings through the full middleware + endpoint pipeline.</summary>
public sealed class EmbeddingsHttpTests : IClassFixture<EmbeddingsHttpTests.EmbeddingsFactory>
{
    private readonly EmbeddingsFactory _factory;

    public EmbeddingsHttpTests(EmbeddingsFactory factory) => _factory = factory;

    [Fact]
    public async Task Embeddings_endpoint_returns_vectors_for_a_configured_embedding_model()
    {
        var response = await AuthedClient().PostAsync("/v1/embeddings", Json(new { model = "text-embed", input = new[] { "hello", "world" } }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("list", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("data").GetArrayLength());
        Assert.Equal("text-embed", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Embeddings_on_a_non_embedding_model_is_rejected()
    {
        var response = await AuthedClient().PostAsync("/v1/embeddings", Json(new { model = "general", input = "hello" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("MODEL_NOT_FOUND", body);
    }

    [Fact]
    public async Task Models_endpoint_lists_the_embedding_model_with_capability()
    {
        var response = await AuthedClient().GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        var embed = doc.RootElement.GetProperty("data").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "text-embed");
        Assert.True(embed.GetProperty("supports_embeddings").GetBoolean());
    }

    private System.Net.Http.HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");
        return client;
    }

    private static StringContent Json<T>(T value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public sealed class EmbeddingsFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                TestConfig.RemoveJsonSources(config);
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Authority"] = "https://issuer.example.test/",
                    ["Jwt:Audience"] = "jarvis",
                    ["Jwt:RequireHttpsMetadata"] = "false",
                    ["Gateway:IdentityBroker:Enabled"] = "false",
                    ["Gateway:ModelDiscovery:Enabled"] = "false",
                    ["Gateway:RequireServiceApiKey"] = "false",
                    ["Gateway:AzureOpenAi:Endpoint"] = "https://test-jarvis.openai.azure.us/",
                    ["Gateway:AzureOpenAi:ApiVersion"] = "2025-04-01-preview",
                    ["Gateway:AzureOpenAi:ApiKey"] = "test-key",
                    ["Gateway:Models:0:Alias"] = "general",
                    ["Gateway:Models:0:ProviderName"] = "azure-openai",
                    ["Gateway:Models:0:AzureDeploymentName"] = "jarvis2-chat",
                    ["Gateway:Models:0:RequiredGroups:0"] = "AI-General-Users",
                    ["Gateway:Models:1:Alias"] = "text-embed",
                    ["Gateway:Models:1:ProviderName"] = "azure-openai",
                    ["Gateway:Models:1:AzureDeploymentName"] = "text-embed",
                    ["Gateway:Models:1:SupportsEmbeddings"] = "true",
                    ["Gateway:Models:1:RequiredGroups:0"] = "AI-General-Users"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticationSchemeProvider>();
                services.AddSingleton<IAuthenticationSchemeProvider, EmbeddingsSchemeProvider>();
                services.RemoveAll<IGraphGroupQueryExecutor>();
                services.AddSingleton<IGraphGroupQueryExecutor, FakeGraphGroupQueryExecutor>();
                services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
                services.AddHttpClient(AzureOpenAiProvider.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubEmbedHandler());
            });
        }

        private sealed class StubEmbedHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"index":0,"embedding":[0.1,0.2]},{"index":1,"embedding":[0.3,0.4]}],"usage":{"prompt_tokens":4,"total_tokens":4}}""",
                        Encoding.UTF8, "application/json")
                });
        }

        private sealed class NoOpAuditLogger : IAuditLogger
        {
            public void Write(GatewayAuditEvent auditEvent) { }
            public void WriteIdentity(IdentityAuditEvent auditEvent) { }
        }

        private sealed class EmbeddingsSchemeProvider : AuthenticationSchemeProvider
        {
            private static readonly AuthenticationScheme TestScheme = new("Bearer", "Bearer", typeof(EmbeddingsAuthHandler));
            public EmbeddingsSchemeProvider(IOptions<AuthenticationOptions> options) : base(options) { }
            public override Task<AuthenticationScheme?> GetSchemeAsync(string name) => Task.FromResult<AuthenticationScheme?>(name == "Bearer" ? TestScheme : null);
            public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);
            public override Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);
            public override Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() => Task.FromResult<IEnumerable<AuthenticationScheme>>([TestScheme]);
        }

        private sealed class EmbeddingsAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var claims = new[] { new Claim("sub", "embed-user"), new Claim("email", "embed@example.test"), new Claim("groups", "AI-General-Users") };
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
            }
        }
    }
}

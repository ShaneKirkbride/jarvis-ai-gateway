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

/// <summary>End-to-end coverage of /v1/completions (FIM/autocomplete) through the full pipeline.</summary>
public sealed class CompletionsHttpTests : IClassFixture<CompletionsHttpTests.CompletionsFactory>
{
    private readonly CompletionsFactory _factory;

    public CompletionsHttpTests(CompletionsFactory factory) => _factory = factory;

    [Fact]
    public async Task Completions_endpoint_returns_text_for_a_fim_model()
    {
        var response = await AuthedClient().PostAsync("/v1/completions", Json(new { model = "code-fim", prompt = "def add(", suffix = "    return a + b" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("text_completion", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal("a, b):", doc.RootElement.GetProperty("choices")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Completions_are_disabled_for_itar_requests()
    {
        var client = AuthedClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Itar-Mode", "true");
        var response = await client.PostAsync("/v1/completions", Json(new { model = "code-fim", prompt = "x" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("AUTOCOMPLETE_DISABLED_FOR_ITAR", body);
    }

    [Fact]
    public async Task Completions_on_a_non_fim_model_are_rejected()
    {
        var response = await AuthedClient().PostAsync("/v1/completions", Json(new { model = "general", prompt = "x" }));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("MODEL_NOT_FOUND", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Models_endpoint_lists_the_fim_model_with_capability()
    {
        var body = await (await AuthedClient().GetAsync("/v1/models")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var fim = doc.RootElement.GetProperty("data").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "code-fim");
        Assert.True(fim.GetProperty("supports_fim").GetBoolean());
    }

    private System.Net.Http.HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");
        return client;
    }

    private static StringContent Json<T>(T value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public sealed class CompletionsFactory : WebApplicationFactory<Program>
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
                    ["Gateway:Models:1:Alias"] = "code-fim",
                    ["Gateway:Models:1:ProviderName"] = "azure-openai",
                    ["Gateway:Models:1:AzureDeploymentName"] = "code-fim",
                    ["Gateway:Models:1:SupportsFim"] = "true",
                    ["Gateway:Models:1:RequiredGroups:0"] = "AI-General-Users"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticationSchemeProvider>();
                services.AddSingleton<IAuthenticationSchemeProvider, CompletionsSchemeProvider>();
                services.RemoveAll<IGraphGroupQueryExecutor>();
                services.AddSingleton<IGraphGroupQueryExecutor, FakeGraphGroupQueryExecutor>();
                services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
                services.AddHttpClient(AzureOpenAiProvider.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubCompletionHandler());
            });
        }

        private sealed class StubCompletionHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"choices":[{"index":0,"text":"a, b):","finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""",
                        Encoding.UTF8, "application/json")
                });
        }

        private sealed class NoOpAuditLogger : IAuditLogger
        {
            public void Write(GatewayAuditEvent auditEvent) { }
            public void WriteIdentity(IdentityAuditEvent auditEvent) { }
        }

        private sealed class CompletionsSchemeProvider : AuthenticationSchemeProvider
        {
            private static readonly AuthenticationScheme TestScheme = new("Bearer", "Bearer", typeof(CompletionsAuthHandler));
            public CompletionsSchemeProvider(IOptions<AuthenticationOptions> options) : base(options) { }
            public override Task<AuthenticationScheme?> GetSchemeAsync(string name) => Task.FromResult<AuthenticationScheme?>(name == "Bearer" ? TestScheme : null);
            public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);
            public override Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);
            public override Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() => Task.FromResult<IEnumerable<AuthenticationScheme>>([TestScheme]);
        }

        private sealed class CompletionsAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var claims = new[] { new Claim("sub", "fim-user"), new Claim("email", "fim@example.test"), new Claim("groups", "AI-General-Users") };
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
            }
        }
    }
}

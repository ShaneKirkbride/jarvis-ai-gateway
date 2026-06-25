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

/// <summary>End-to-end coverage of the Anthropic-compatible /v1/messages endpoint (Phase 4).</summary>
public sealed class MessagesHttpTests : IClassFixture<MessagesHttpTests.MessagesFactory>
{
    private readonly MessagesFactory _factory;

    public MessagesHttpTests(MessagesFactory factory) => _factory = factory;

    [Fact]
    public async Task Messages_endpoint_returns_an_anthropic_message()
    {
        var payload = new
        {
            model = "general",
            max_tokens = 256,
            system = "You are helpful.",
            messages = new[] { new { role = "user", content = "Hello" } }
        };
        var response = await AuthedClient().PostAsync("/v1/messages", Json(payload));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.Equal("Hello from the gateway.", root.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public async Task Messages_endpoint_rejects_streaming()
    {
        var payload = new { model = "general", max_tokens = 16, stream = true, messages = new[] { new { role = "user", content = "hi" } } };
        var response = await AuthedClient().PostAsync("/v1/messages", Json(payload));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request_error", body);
    }

    [Fact]
    public async Task Messages_endpoint_rejects_image_content()
    {
        var payload = new
        {
            model = "general",
            max_tokens = 16,
            messages = new object[]
            {
                new { role = "user", content = new object[] { new { type = "image", source = new { type = "base64", media_type = "image/png", data = "AAAA" } } } }
            }
        };
        var response = await AuthedClient().PostAsync("/v1/messages", Json(payload));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("only text content", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Messages_endpoint_denies_unknown_model()
    {
        var payload = new { model = "does-not-exist", max_tokens = 16, messages = new[] { new { role = "user", content = "hi" } } };
        var response = await AuthedClient().PostAsync("/v1/messages", Json(payload));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("permission_error", body);
    }

    [Fact]
    public async Task Messages_endpoint_requires_authentication()
    {
        var payload = new { model = "general", max_tokens = 16, messages = new[] { new { role = "user", content = "hi" } } };
        var response = await _factory.CreateClient().PostAsync("/v1/messages", Json(payload));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private System.Net.Http.HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");
        return client;
    }

    private static StringContent Json<T>(T value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public sealed class MessagesFactory : WebApplicationFactory<Program>
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
                    ["Gateway:Models:0:RequiredGroups:0"] = "AI-General-Users"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticationSchemeProvider>();
                services.AddSingleton<IAuthenticationSchemeProvider, MessagesSchemeProvider>();
                services.RemoveAll<IGraphGroupQueryExecutor>();
                services.AddSingleton<IGraphGroupQueryExecutor, FakeGraphGroupQueryExecutor>();
                services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
                services.AddHttpClient(AzureOpenAiProvider.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubChatHandler());
            });
        }

        private sealed class StubChatHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"choices":[{"index":0,"message":{"role":"assistant","content":"Hello from the gateway."},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}""",
                        Encoding.UTF8, "application/json")
                });
        }

        private sealed class NoOpAuditLogger : IAuditLogger
        {
            public void Write(GatewayAuditEvent auditEvent) { }
            public void WriteIdentity(IdentityAuditEvent auditEvent) { }
        }

        private sealed class MessagesSchemeProvider : AuthenticationSchemeProvider
        {
            private static readonly AuthenticationScheme TestScheme = new("Bearer", "Bearer", typeof(MessagesAuthHandler));
            public MessagesSchemeProvider(IOptions<AuthenticationOptions> options) : base(options) { }
            public override Task<AuthenticationScheme?> GetSchemeAsync(string name) => Task.FromResult<AuthenticationScheme?>(name == "Bearer" ? TestScheme : null);
            public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);
            public override Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);
            public override Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() => Task.FromResult<IEnumerable<AuthenticationScheme>>([TestScheme]);
        }

        private sealed class MessagesAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                if (!Request.Headers.ContainsKey("Authorization"))
                {
                    return Task.FromResult(AuthenticateResult.Fail("missing"));
                }

                var claims = new[] { new Claim("sub", "msg-user"), new Claim("email", "msg@example.test"), new Claim("groups", "AI-General-Users") };
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
            }
        }
    }
}

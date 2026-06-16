using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
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

public sealed class OpenWebUiContractTests : IClassFixture<OpenWebUiContractTests.GatewayFactory>
{
    private readonly GatewayFactory _factory;

    public OpenWebUiContractTests(GatewayFactory factory) => _factory = factory;

    [Fact]
    public async Task Models_response_shape_filters_by_groups()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");

        var response = await client.GetAsync("/v1/models");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("list", doc.RootElement.GetProperty("object").GetString());
        var data = doc.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Contains(data, e => e.GetProperty("id").GetString() == "general");
        Assert.DoesNotContain(data, e => e.GetProperty("id").GetString() == "restricted");
    }

    [Fact]
    public async Task Chat_completion_success_shape_is_openai_compatible()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");
        var response = await client.PostAsync("/v1/chat/completions", Json(new
        {
            model = "general",
            messages = new[] { new { role = "user", content = "hello" } }
        }));

        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal("general", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("assistant", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("contract response", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.True(doc.RootElement.TryGetProperty("usage", out _));
    }

    [Fact]
    public async Task Invalid_request_returns_400_openai_error_not_502()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");
        var response = await client.PostAsync("/v1/chat/completions", Json(new
        {
            model = "general",
            messages = new[] { new { role = "user", content = new { type = "image_url", image_url = new { url = "https://example.invalid/a.png" } } } }
        }));

        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("invalid_request_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("unsupported_content", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Policy_denial_and_unknown_model_use_openai_error_shape()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");

        var denied = await client.PostAsync("/v1/chat/completions", Json(new
        {
            model = "restricted",
            messages = new[] { new { role = "user", content = "hello" } }
        }));
        var unknown = await client.PostAsync("/v1/chat/completions", Json(new
        {
            model = "missing",
            messages = new[] { new { role = "user", content = "hello" } }
        }));

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Contains("USER_GROUP_DENIED", await denied.Content.ReadAsStringAsync());
        Assert.Contains("MODEL_NOT_FOUND", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stream_true_falls_back_deterministically_when_configured()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test");
        var response = await client.PostAsync("/v1/chat/completions", Json(new
        {
            model = "general",
            stream = true,
            messages = new[] { new { role = "user", content = "hello" } }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Service_key_and_forwarded_user_token_path_is_accepted()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:RequireServiceApiKey"] = "true",
                ["Gateway:ServiceApiKey"] = "test-service-key"
            });
        }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Gateway-Key", "test-service-key");
        client.DefaultRequestHeaders.Add("X-Jarvis-User-Token", "Bearer forwarded-test");

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static StringContent Json<T>(T value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public sealed class GatewayFactory : WebApplicationFactory<Program>
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
                    // These contract tests cover the legacy JwtBearer + RequiredGroups path;
                    // the broker has its own dedicated test files.  Opt the broker out
                    // explicitly so this test app boots after the broker default flipped on.
                    ["Gateway:IdentityBroker:Enabled"] = "false",
                    ["Gateway:ModelDiscovery:Enabled"] = "false",
                    ["Gateway:Models:0:Alias"] = "general",
                    ["Gateway:Models:0:BedrockModelId"] = "anthropic.claude-3-haiku-20240307-v1:0",
                    ["Gateway:Models:0:RequiredGroups:0"] = "AI-General-Users",
                    ["Gateway:Models:0:SupportsConverse"] = "true",
                    ["Gateway:Models:1:Alias"] = "restricted",
                    ["Gateway:Models:1:BedrockModelId"] = "anthropic.claude-3-sonnet-20240229-v1:0",
                    ["Gateway:Models:1:RequiredGroups:0"] = "Restricted-Users",
                    ["Gateway:Models:1:SupportsConverse"] = "true"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticationSchemeProvider>();
                services.AddSingleton<IAuthenticationSchemeProvider, TestSchemeProvider>();
                services.RemoveAll<IBedrockInvocationStrategy>();
                services.AddSingleton<IBedrockInvocationStrategy, FakeStrategy>();
                services.AddSingleton<IAuditLogger, InMemoryAuditLogger>();
                // The broker is opt-out via Gateway:IdentityBroker:Enabled=false above, but
                // Program.cs captures that flag synchronously before WebApplicationFactory's
                // deferred config callbacks are merged, so the broker DI graph is still
                // registered.  Replace the real Microsoft Graph adapter with a fake so
                // host startup does not depend on real Entra credentials being present.
                services.RemoveAll<IGraphGroupQueryExecutor>();
                services.AddSingleton<IGraphGroupQueryExecutor, FakeGraphGroupQueryExecutor>();
            });
        }
    }

    private sealed class TestSchemeProvider : AuthenticationSchemeProvider
    {
        private static readonly AuthenticationScheme TestScheme = new("Bearer", "Bearer", typeof(TestAuthHandler));

        public TestSchemeProvider(IOptions<AuthenticationOptions> options) : base(options) { }

        public override Task<AuthenticationScheme?> GetSchemeAsync(string name) =>
            Task.FromResult<AuthenticationScheme?>(name == "Bearer" ? TestScheme : null);

        public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);

        public override Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);

        public override Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() =>
            Task.FromResult<IEnumerable<AuthenticationScheme>>([TestScheme]);
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim("sub", "contract-user"),
                new Claim("email", "contract-user@example.test"),
                new Claim("groups", "AI-General-Users")
            };
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class FakeStrategy : IBedrockInvocationStrategy
    {
        public string Name => "fake-provider";
        public bool CanHandle(GatewayModel model, AiChatRequest request) => model.Id == "general";
        public Task<AiChatResult> InvokeAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new AiChatResult("contract response", new TokenUsage(2, 3, 5), "stop", new ProviderInvocationMetadata("fake", Name, 1, "provider-request-id")));
    }

    private sealed class InMemoryAuditLogger : IAuditLogger
    {
        public void Write(GatewayAuditEvent auditEvent) { }
        public void WriteIdentity(IdentityAuditEvent auditEvent) { }
    }
}

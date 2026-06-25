using System.Net;
using System.Net.Http.Headers;
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
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

/// <summary>
/// End-to-end coverage of developer API-key authentication through the full middleware + endpoint
/// pipeline: a valid jrvs_ key authenticates and is authorized by the unchanged policy engine;
/// invalid/expired/revoked keys fail closed; ITAR/group authorization is NOT bypassed; audit
/// records key id (never the raw key); and /v1/models returns additive capability metadata while
/// preserving the OpenAI list shape.
/// </summary>
public sealed class DeveloperApiKeyAuthHttpTests : IClassFixture<DeveloperApiKeyAuthHttpTests.DeveloperKeyFactory>
{
    private readonly DeveloperKeyFactory _factory;

    public DeveloperApiKeyAuthHttpTests(DeveloperKeyFactory factory) => _factory = factory;

    [Fact]
    public async Task Valid_developer_key_completes_chat_and_audits_key_id_not_raw_key()
    {
        DeveloperKeyFactory.Audit.Events.Clear();
        var response = await Post("general", DeveloperKeyFactory.ValidKey);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());

        var audit = Assert.Single(DeveloperKeyFactory.Audit.Events, e => e.Decision == "ALLOW");
        Assert.Equal("developer_api_key", audit.AuthType);
        Assert.Equal("k1", audit.ApiKeyId);
        // The raw key must never appear in audit material.
        Assert.DoesNotContain(DeveloperKeyFactory.ValidKey, JsonSerializer.Serialize(audit));
    }

    [Theory]
    [InlineData("jrvs_wrong-key")]              // unknown
    [InlineData("jrvs_revoked")]                // revoked
    [InlineData("jrvs_expired")]                // expired
    public async Task Invalid_keys_fail_closed_with_generic_401(string key)
    {
        var response = await Post("general", key);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("INVALID_API_KEY", body);
    }

    [Fact]
    public async Task No_credential_is_unauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/v1/chat/completions", JsonBody("general"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Developer_key_cannot_reach_model_requiring_a_group_the_owner_lacks()
    {
        // The key owner resolves to AI-General-Users only; "restricted" requires a different group.
        // Proves ITAR/group authorization is enforced unchanged for API-key principals (no bypass).
        var response = await Post("restricted", DeveloperKeyFactory.ValidKey);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("USER_GROUP_DENIED", body);
    }

    [Fact]
    public async Task Models_endpoint_adds_capability_metadata_and_preserves_shape()
    {
        var client = AuthedClient(DeveloperKeyFactory.ValidKey);
        var response = await client.GetAsync("/v1/models");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        var general = doc.RootElement.GetProperty("data").EnumerateArray()
            .Single(m => m.GetProperty("id").GetString() == "general");

        // Existing OpenAI shape preserved.
        Assert.Equal("general", general.GetProperty("id").GetString());
        Assert.False(string.IsNullOrEmpty(general.GetProperty("owned_by").GetString()));
        // Additive capability metadata present.
        Assert.True(general.GetProperty("supports_chat").GetBoolean());
        Assert.True(general.TryGetProperty("supports_streaming", out _));
        Assert.True(general.TryGetProperty("approved_for_itar", out _));
        Assert.Equal("aws-bedrock", general.GetProperty("provider").GetString());
    }

    private Task<HttpResponseMessage> Post(string model, string apiKey) =>
        AuthedClient(apiKey).PostAsync("/v1/chat/completions", JsonBody(model));

    private System.Net.Http.HttpClient AuthedClient(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private static StringContent JsonBody(string model) => new(
        JsonSerializer.Serialize(new { model, messages = new[] { new { role = "user", content = "hello" } } }),
        Encoding.UTF8, "application/json");

    public sealed class DeveloperKeyFactory : WebApplicationFactory<Program>
    {
        public const string Pepper = "test-developer-pepper-secret";
        public const string ValidKey = "jrvs_validdevkey";
        public static readonly CapturingAudit Audit = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                TestConfig.RemoveJsonSources(config);
                var hasher = new DeveloperApiKeyHasher(MsOptions.Create(
                    new GatewayOptions { DeveloperAuth = new DeveloperAuthOptions { HashPepper = Pepper } }));

                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Authority"] = "https://issuer.example.test/",
                    ["Jwt:Audience"] = "jarvis",
                    ["Jwt:RequireHttpsMetadata"] = "false",
                    ["Gateway:IdentityBroker:Enabled"] = "false",
                    ["Gateway:ModelDiscovery:Enabled"] = "false",
                    ["Gateway:RequireServiceApiKey"] = "false",
                    ["Gateway:DeveloperAuth:Enabled"] = "true",
                    ["Gateway:DeveloperAuth:HashPepper"] = Pepper,
                    ["Gateway:DeveloperAuth:Keys:0:KeyId"] = "k1",
                    ["Gateway:DeveloperAuth:Keys:0:KeyHash"] = hasher.Hash(ValidKey),
                    ["Gateway:DeveloperAuth:Keys:0:OwnerSubject"] = "dev-subject",
                    ["Gateway:DeveloperAuth:Keys:0:OwnerEmail"] = "dev@example.test",
                    ["Gateway:DeveloperAuth:Keys:1:KeyId"] = "k2",
                    ["Gateway:DeveloperAuth:Keys:1:KeyHash"] = hasher.Hash("jrvs_revoked"),
                    ["Gateway:DeveloperAuth:Keys:1:OwnerSubject"] = "dev-subject",
                    ["Gateway:DeveloperAuth:Keys:1:Revoked"] = "true",
                    ["Gateway:DeveloperAuth:Keys:2:KeyId"] = "k3",
                    ["Gateway:DeveloperAuth:Keys:2:KeyHash"] = hasher.Hash("jrvs_expired"),
                    ["Gateway:DeveloperAuth:Keys:2:OwnerSubject"] = "dev-subject",
                    ["Gateway:DeveloperAuth:Keys:2:ExpiresUtc"] = "2000-01-01T00:00:00Z",
                    ["Gateway:Models:0:Alias"] = "general",
                    ["Gateway:Models:0:BedrockModelId"] = "anthropic.claude-3-haiku-20240307-v1:0",
                    ["Gateway:Models:0:RequiredGroups:0"] = "AI-General-Users",
                    ["Gateway:Models:0:SupportsConverse"] = "true",
                    ["Gateway:Models:1:Alias"] = "restricted",
                    ["Gateway:Models:1:BedrockModelId"] = "anthropic.claude-3-sonnet-20240229-v1:0",
                    ["Gateway:Models:1:RequiredGroups:0"] = "AI-Restricted-Users",
                    ["Gateway:Models:1:SupportsConverse"] = "true"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticationSchemeProvider>();
                services.AddSingleton<IAuthenticationSchemeProvider, NoOpSchemeProvider>();
                // Developer auth resolves the owner's live groups via IGraphGroupResolver. Register a
                // fake so the test does not depend on the identity broker / Microsoft Graph.
                services.RemoveAll<IGraphGroupResolver>();
                services.AddSingleton<IGraphGroupResolver, FakeResolver>();
                services.RemoveAll<IBedrockInvocationStrategy>();
                services.AddSingleton<IBedrockInvocationStrategy, FakeBedrockStrategy>();
                services.RemoveAll<IBedrockStreamingStrategy>();
                Audit.Events.Clear();
                services.AddSingleton<IAuditLogger>(Audit);
            });
        }

        private sealed class FakeResolver : IGraphGroupResolver
        {
            public Task<GraphLookupResult> ResolveAsync(string canonicalSubject, CancellationToken cancellationToken) =>
                Task.FromResult(GraphLookupResult.Success(
                    new HashSet<DirectoryGroupRef> { new("00000000-0000-0000-0000-000000000001", "AI-General-Users") },
                    "oid-dev", wasCached: false));
        }

        private sealed class FakeBedrockStrategy : IBedrockInvocationStrategy
        {
            public string Name => "fake-bedrock";
            public bool CanHandle(GatewayModel model, AiChatRequest request) => true;
            public Task<AiChatResult> InvokeAsync(GatewayModel model, AiChatRequest request, RequestContext context, CancellationToken cancellationToken) =>
                Task.FromResult(new AiChatResult("dev-response", new TokenUsage(1, 1, 2), "stop", new ProviderInvocationMetadata("aws-bedrock", Name, 1)));
        }

        private sealed class NoOpSchemeProvider : AuthenticationSchemeProvider
        {
            private static readonly AuthenticationScheme TestScheme = new("Bearer", "Bearer", typeof(NoOpAuthHandler));
            public NoOpSchemeProvider(IOptions<AuthenticationOptions> options) : base(options) { }
            public override Task<AuthenticationScheme?> GetSchemeAsync(string name) => Task.FromResult<AuthenticationScheme?>(name == "Bearer" ? TestScheme : null);
            public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);
            public override Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() => Task.FromResult<AuthenticationScheme?>(TestScheme);
            public override Task<IEnumerable<AuthenticationScheme>> GetAllSchemesAsync() => Task.FromResult<IEnumerable<AuthenticationScheme>>([TestScheme]);
        }

        // Never authenticates: developer auth (a custom middleware) sets the principal upstream;
        // this handler only exists so the default authentication middleware does not attempt real
        // JWT validation (and never clobbers the developer principal).
        private sealed class NoOpAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
        }

        public sealed class CapturingAudit : IAuditLogger
        {
            public List<GatewayAuditEvent> Events { get; } = [];
            public void Write(GatewayAuditEvent auditEvent) => Events.Add(auditEvent);
            public void WriteIdentity(IdentityAuditEvent auditEvent) { }
        }
    }
}

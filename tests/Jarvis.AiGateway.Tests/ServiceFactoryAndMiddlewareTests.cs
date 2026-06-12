using System.Security.Claims;
using System.Text.Json;
using Jarvis.AiGateway.Middleware;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class ServiceFactoryAndMiddlewareTests
{
    [Fact]
    public void User_context_factory_extracts_subject_email_groups_roles_and_claims()
    {
        var principal = Principal(
            new Claim("sub", "subject-1"),
            new Claim("email", "user@example.test"),
            new Claim("groups", "[\"AI-General-Users\",\"ITAR\"]"),
            new Claim(ClaimTypes.Role, "Administrators"));
        var factory = new UserContextFactory(MsOptions.Create(new GatewayOptions()));

        var user = factory.Create(principal);

        Assert.Equal("subject-1", user.Subject);
        Assert.Equal("user@example.test", user.Email);
        Assert.Contains("AI-General-Users", user.Groups);
        Assert.Contains("ITAR", user.Groups);
        Assert.Contains("Administrators", user.Groups);
        Assert.Equal("subject-1", user.Claims["sub"]);
    }

    [Fact]
    public void User_context_factory_falls_back_to_name_identifier_name_email_and_subject()
    {
        var namedIdentity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "name-id")], "test", ClaimTypes.Name, ClaimTypes.Role);
        namedIdentity.AddClaim(new Claim(ClaimTypes.Name, "identity-name"));
        var factory = new UserContextFactory(MsOptions.Create(new GatewayOptions { EmailClaimNames = ["missing"] }));

        var user = factory.Create(new ClaimsPrincipal(namedIdentity));

        Assert.Equal("name-id", user.Subject);
        Assert.Equal("name-id", user.Email);
    }

    [Fact]
    public void Request_context_factory_prefers_headers_over_metadata_and_detects_itar_label()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-1" };
        context.Request.Headers["X-Correlation-Id"] = "corr-header";
        context.Request.Headers["X-Jarvis-Workspace-Id"] = "workspace-header";
        context.Request.Headers["X-Jarvis-Data-Label"] = "itar-controlled";
        context.Request.Headers["X-Jarvis-Itar-Mode"] = "false";
        var request = Request("model", metadata: new Dictionary<string, JsonElement>
        {
            ["workspace_id"] = Json("workspace-metadata"),
            ["data_label"] = Json("NON_ITAR"),
            ["itar_mode"] = Json(true)
        });

        var result = new RequestContextFactory().Create(context, request);

        Assert.Equal("trace-1", result.RequestId);
        Assert.Equal("corr-header", result.CorrelationId);
        Assert.Equal("workspace-header", result.WorkspaceId);
        Assert.Equal("ITAR-CONTROLLED", result.DataLabel);
        Assert.True(result.ItarMode);
    }

    [Fact]
    public void Request_context_factory_uses_metadata_and_defaults_when_headers_are_missing()
    {
        var withMetadata = new DefaultHttpContext { TraceIdentifier = "trace-2" };
        withMetadata.Request.Headers["X-Request-Id"] = "request-header";
        var request = Request("model", metadata: new Dictionary<string, JsonElement>
        {
            ["workspace"] = Json("workspace-metadata"),
            ["data_label"] = Json(123),
            ["itar_mode"] = Json(true),
            ["ignored"] = Json(new { nested = true })
        });

        var metadataResult = new RequestContextFactory().Create(withMetadata, request);
        var ignoredMetadataResult = new RequestContextFactory().Create(new DefaultHttpContext { TraceIdentifier = "trace-ignored" }, new OpenAiChatCompletionRequest { Metadata = new Dictionary<string, JsonElement> { ["workspace"] = Json(new { nested = true }) } });
        var falseItarMetadataResult = new RequestContextFactory().Create(new DefaultHttpContext { TraceIdentifier = "trace-false" }, new OpenAiChatCompletionRequest { Metadata = new Dictionary<string, JsonElement> { ["itar_mode"] = Json(false) } });
        var defaultResult = new RequestContextFactory().Create(new DefaultHttpContext { TraceIdentifier = "trace-3" });

        Assert.Equal("request-header", metadataResult.CorrelationId);
        Assert.Equal("workspace-metadata", metadataResult.WorkspaceId);
        Assert.Equal("123", metadataResult.DataLabel);
        Assert.True(metadataResult.ItarMode);
        Assert.Equal("unknown", ignoredMetadataResult.WorkspaceId);
        Assert.False(falseItarMetadataResult.ItarMode);
        Assert.Equal("trace-3", defaultResult.CorrelationId);
        Assert.Equal("unknown", defaultResult.WorkspaceId);
        Assert.Equal("NON_ITAR", defaultResult.DataLabel);
    }

    [Fact]
    public async Task Service_api_key_middleware_allows_unrequired_health_and_valid_key_paths()
    {
        var calls = 0;
        RequestDelegate next = _ => { calls++; return Task.CompletedTask; };

        await new ServiceApiKeyMiddleware(next, MsOptions.Create(new GatewayOptions { RequireServiceApiKey = false })).InvokeAsync(new DefaultHttpContext());
        await new ServiceApiKeyMiddleware(next, MsOptions.Create(new GatewayOptions { RequireServiceApiKey = true })).InvokeAsync(ContextForPath("/healthz"));
        await new ServiceApiKeyMiddleware(next, MsOptions.Create(new GatewayOptions { RequireServiceApiKey = true })).InvokeAsync(ContextForPath("/readyz"));
        var valid = ContextForPath("/v1/models");
        valid.Request.Headers["X-Jarvis-Gateway-Key"] = "secret";
        await new ServiceApiKeyMiddleware(next, MsOptions.Create(new GatewayOptions { RequireServiceApiKey = true, ServiceApiKey = "secret" })).InvokeAsync(valid);

        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Service_api_key_middleware_fails_closed_when_missing_config_or_invalid_key()
    {
        var missingConfig = ContextForPath("/v1/models");
        await new ServiceApiKeyMiddleware(_ => throw new InvalidOperationException("next should not run"), MsOptions.Create(new GatewayOptions { RequireServiceApiKey = true })).InvokeAsync(missingConfig);

        var invalid = ContextForPath("/v1/models");
        invalid.Request.Headers["X-Jarvis-Gateway-Key"] = "wrong";
        await new ServiceApiKeyMiddleware(_ => throw new InvalidOperationException("next should not run"), MsOptions.Create(new GatewayOptions { RequireServiceApiKey = true, ServiceApiKey = "secret" })).InvokeAsync(invalid);

        Assert.Equal(StatusCodes.Status500InternalServerError, missingConfig.Response.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, invalid.Response.StatusCode);
    }

    [Fact]
    public async Task Correlation_id_middleware_sets_response_header_and_calls_next()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace" };
        context.Request.Headers["X-Correlation-Id"] = "corr";
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton(NullLogger<CorrelationIdMiddleware>.Instance)
            .BuildServiceProvider();
        var called = false;

        await new CorrelationIdMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context);

        Assert.True(called);
        Assert.Equal("corr", context.Response.Headers["X-Correlation-Id"].ToString());
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    private static DefaultHttpContext ContextForPath(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static OpenAiChatCompletionRequest Request(string model, Dictionary<string, JsonElement>? metadata = null) => new()
    {
        Model = model,
        Messages = [new OpenAiMessage { Role = "user", Content = Json("hello") }],
        Metadata = metadata
    };

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
}

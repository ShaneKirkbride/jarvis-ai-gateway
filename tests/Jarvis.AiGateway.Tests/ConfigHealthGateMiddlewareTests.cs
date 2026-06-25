using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Security;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jarvis.AiGateway.Tests;

public sealed class ConfigHealthGateMiddlewareTests
{
    [Fact]
    public async Task Protected_route_returns_503_envelope_when_not_ready()
    {
        var (status, body, nextCalled) = await InvokeAsync("/v1/chat/completions", ready: false);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.False(nextCalled);
        Assert.Contains("GATEWAY_CONFIG_INVALID", body);
        Assert.Contains("service_unavailable", body);
        Assert.Contains("Gateway is not ready.", body);
    }

    [Fact]
    public async Task Protected_route_passes_through_when_ready()
    {
        var (status, _, nextCalled) = await InvokeAsync("/v1/chat/completions", ready: true);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Health_endpoints_are_not_gated_even_when_not_ready()
    {
        var (_, _, nextCalled) = await InvokeAsync("/healthz/ready", ready: false);

        Assert.True(nextCalled);
    }

    private static async Task<(int Status, string Body, bool NextCalled)> InvokeAsync(string path, bool ready)
    {
        var nextCalled = false;
        var middleware = new ConfigHealthGateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, new FakeConfigHealth(ready));

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, body, nextCalled);
    }

    private sealed class FakeConfigHealth(bool ready) : IConfigHealth
    {
        public bool IsReady => ready;
        public IReadOnlyList<ConfigValidationProblem> Problems => [];
    }
}

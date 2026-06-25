using System.Security.Claims;
using Jarvis.AiGateway.Models;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Security;
using Jarvis.AiGateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

public sealed class DeveloperApiKeyMiddlewareTests
{
    [Fact]
    public async Task Disabled_passes_through_without_calling_authenticator()
    {
        var auth = new FakeAuthenticator(Authenticated());
        var (ctx, nextCalled) = await InvokeAsync(auth, enabled: false, authorization: "Bearer jrvs_x");

        Assert.True(nextCalled());
        Assert.False(auth.Called);
        Assert.False(DeveloperApiKeyContext.IsAuthenticated(ctx));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer eyJ.jwt.token")] // legacy JWT bearer: not a jrvs_ key
    [InlineData("jrvs_without_bearer_scheme")]
    public async Task Non_developer_bearer_passes_through(string? authorization)
    {
        var auth = new FakeAuthenticator(Authenticated());
        var (_, nextCalled) = await InvokeAsync(auth, enabled: true, authorization: authorization);

        Assert.True(nextCalled());
        Assert.False(auth.Called);
    }

    [Fact]
    public async Task Valid_key_sets_principal_and_flag_and_continues()
    {
        var auth = new FakeAuthenticator(Authenticated());
        var (ctx, nextCalled) = await InvokeAsync(auth, enabled: true, authorization: "Bearer jrvs_valid");

        Assert.True(auth.Called);
        Assert.True(nextCalled());
        Assert.True(DeveloperApiKeyContext.IsAuthenticated(ctx));
        Assert.Equal("dev", ctx.User.FindFirstValue("sub"));
    }

    [Theory]
    [InlineData(DeveloperApiKeyOutcome.InvalidKey)]
    [InlineData(DeveloperApiKeyOutcome.Expired)]
    [InlineData(DeveloperApiKeyOutcome.Revoked)]
    [InlineData(DeveloperApiKeyOutcome.Malformed)]
    public async Task Bad_keys_return_generic_401_without_leaking_reason(DeveloperApiKeyOutcome outcome)
    {
        var auth = new FakeAuthenticator(DeveloperApiKeyAuthResult.Fail(outcome, "fp1234567890ab", keyId: "k1"));
        var (ctx, nextCalled, body) = await InvokeReadingBodyAsync(auth, "Bearer jrvs_bad");

        Assert.False(nextCalled());
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Contains("INVALID_API_KEY", body);
        // The generic body must not reveal which condition failed.
        Assert.DoesNotContain("expired", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("revoked", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolution_unavailable_returns_503_and_owner_not_found_returns_403()
    {
        var (ctx503, _, body503) = await InvokeReadingBodyAsync(
            new FakeAuthenticator(DeveloperApiKeyAuthResult.Fail(DeveloperApiKeyOutcome.ResolutionUnavailable, "fp")), "Bearer jrvs_x");
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx503.Response.StatusCode);
        Assert.Contains("IDENTITY_RESOLUTION_UNAVAILABLE", body503);

        var (ctx403, _, body403) = await InvokeReadingBodyAsync(
            new FakeAuthenticator(DeveloperApiKeyAuthResult.Fail(DeveloperApiKeyOutcome.OwnerNotFound, "fp")), "Bearer jrvs_x");
        Assert.Equal(StatusCodes.Status403Forbidden, ctx403.Response.StatusCode);
        Assert.Contains("IDENTITY_USER_NOT_FOUND", body403);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static DeveloperApiKeyAuthResult Authenticated() =>
        DeveloperApiKeyAuthResult.Success(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "dev")], DeveloperApiKeyClaims.AuthenticationScheme)),
            "k1", "fp1234567890ab");

    private static async Task<(HttpContext Ctx, Func<bool> NextCalled)> InvokeAsync(
        IDeveloperApiKeyAuthenticator authenticator, bool enabled, string? authorization)
    {
        var called = false;
        var middleware = new DeveloperApiKeyMiddleware(
            _ => { called = true; return Task.CompletedTask; },
            authenticator,
            MsOptions.Create(new GatewayOptions { DeveloperAuth = new DeveloperAuthOptions { Enabled = enabled, KeyPrefix = "jrvs_" } }),
            new NoOpAudit(),
            new FakeSubjectHasher());

        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        if (authorization is not null) ctx.Request.Headers.Authorization = authorization;

        await middleware.InvokeAsync(ctx);
        return (ctx, () => called);
    }

    private static async Task<(HttpContext Ctx, Func<bool> NextCalled, string Body)> InvokeReadingBodyAsync(
        IDeveloperApiKeyAuthenticator authenticator, string authorization)
    {
        var (ctx, nextCalled) = await InvokeAsync(authenticator, enabled: true, authorization);
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx, nextCalled, body);
    }

    private sealed class FakeAuthenticator(DeveloperApiKeyAuthResult result) : IDeveloperApiKeyAuthenticator
    {
        public bool Called { get; private set; }
        public Task<DeveloperApiKeyAuthResult> AuthenticateAsync(string presentedKey, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(result);
        }
    }

    private sealed class NoOpAudit : IAuditLogger
    {
        public void Write(GatewayAuditEvent auditEvent) { }
        public void WriteIdentity(IdentityAuditEvent auditEvent) { }
    }

    private sealed class FakeSubjectHasher : ISubjectHasher
    {
        public string? Hash(string? value) => value is null ? null : "h:" + value;
    }
}

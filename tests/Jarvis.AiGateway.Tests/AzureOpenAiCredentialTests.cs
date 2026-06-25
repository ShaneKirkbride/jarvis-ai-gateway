using Azure.Core;
using Jarvis.AiGateway.Options;
using Jarvis.AiGateway.Services;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Jarvis.AiGateway.Tests;

public sealed class AzureOpenAiCredentialTests
{
    [Fact]
    public async Task ApiKey_credential_sets_api_key_header()
    {
        var credential = new ApiKeyAzureOpenAiCredential(MsOptions.Create(new AzureOpenAiOptions { ApiKey = "secret-key" }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://x.openai.azure.us/");

        await credential.ApplyAsync(request, CancellationToken.None);

        Assert.True(request.Headers.TryGetValues("api-key", out var values));
        Assert.Equal("secret-key", Assert.Single(values!));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task ApiKey_credential_throws_when_key_missing()
    {
        var credential = new ApiKeyAzureOpenAiCredential(MsOptions.Create(new AzureOpenAiOptions { ApiKey = "" }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://x.openai.azure.us/");

        await Assert.ThrowsAsync<InvalidOperationException>(() => credential.ApplyAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task ManagedIdentity_credential_sets_bearer_token_with_derived_gov_scope()
    {
        var token = new FakeTokenCredential();
        var credential = new ManagedIdentityAzureOpenAiCredential(
            token,
            MsOptions.Create(new AzureOpenAiOptions { Endpoint = "https://azr-gov-jarvis2-openai.openai.azure.us/" }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://x.openai.azure.us/");

        await credential.ApplyAsync(request, CancellationToken.None);

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("fake-token", request.Headers.Authorization?.Parameter);
        Assert.Equal(AzureOpenAiScopes.UsGovernment, token.RequestedScope);
    }

    [Fact]
    public async Task ManagedIdentity_credential_uses_explicit_scope_override()
    {
        var token = new FakeTokenCredential();
        var credential = new ManagedIdentityAzureOpenAiCredential(
            token,
            MsOptions.Create(new AzureOpenAiOptions { Endpoint = "https://x.openai.azure.us/", ManagedIdentityScope = "custom/.default" }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://x.openai.azure.us/");

        await credential.ApplyAsync(request, CancellationToken.None);

        Assert.Equal("custom/.default", token.RequestedScope);
    }

    [Theory]
    [InlineData("https://res.openai.azure.us/", "https://cognitiveservices.azure.us/.default")]
    [InlineData("https://res.openai.azure.com/", "https://cognitiveservices.azure.com/.default")]
    [InlineData("", "https://cognitiveservices.azure.com/.default")]
    [InlineData("not a url", "https://cognitiveservices.azure.com/.default")]
    public void ForEndpoint_derives_scope_from_host(string endpoint, string expected)
    {
        Assert.Equal(expected, AzureOpenAiScopes.ForEndpoint(endpoint));
    }

    private sealed class FakeTokenCredential : TokenCredential
    {
        public string? RequestedScope { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            RequestedScope = requestContext.Scopes.FirstOrDefault();
            return new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            RequestedScope = requestContext.Scopes.FirstOrDefault();
            return new ValueTask<AccessToken>(new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}

using System.Net.Http.Headers;
using Azure.Core;
using Jarvis.AiGateway.Options;
using Microsoft.Extensions.Options;

namespace Jarvis.AiGateway.Services;

/// <summary>
/// Applies authentication to an outbound Azure OpenAI request.  Abstracted so the API-key and
/// Managed Identity paths are interchangeable and independently testable, and so a future auth
/// mode can be added without touching <see cref="AzureOpenAiProvider"/>.
/// </summary>
public interface IAzureOpenAiCredential
{
    Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

/// <summary>Static <c>api-key</c> header authentication.</summary>
public sealed class ApiKeyAzureOpenAiCredential(IOptions<AzureOpenAiOptions> options) : IAzureOpenAiCredential
{
    private readonly AzureOpenAiOptions _options = options.Value;

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("Azure OpenAI API key is not configured.");
        }

        // Azure data-plane expects the raw key on the "api-key" header (not an Authorization bearer).
        request.Headers.Remove("api-key");
        request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);
        return Task.CompletedTask;
    }
}

/// <summary>
/// AAD bearer-token authentication via a <see cref="TokenCredential"/> (e.g. a managed identity).
/// The token scope is taken from <see cref="AzureOpenAiOptions.ManagedIdentityScope"/> when set,
/// otherwise derived from the endpoint host so Government endpoints get the correct audience.
/// </summary>
public sealed class ManagedIdentityAzureOpenAiCredential(
    TokenCredential tokenCredential,
    IOptions<AzureOpenAiOptions> options) : IAzureOpenAiCredential
{
    private readonly AzureOpenAiOptions _options = options.Value;

    public async Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(_options.ManagedIdentityScope)
            ? AzureOpenAiScopes.ForEndpoint(_options.Endpoint)
            : _options.ManagedIdentityScope;

        var token = await tokenCredential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }
}

/// <summary>Derives the Cognitive Services AAD token scope from an Azure OpenAI endpoint host.</summary>
public static class AzureOpenAiScopes
{
    public const string Commercial = "https://cognitiveservices.azure.com/.default";
    public const string UsGovernment = "https://cognitiveservices.azure.us/.default";

    public static string ForEndpoint(string? endpoint)
    {
        // Government data-plane hosts end in ".azure.us"; everything else uses the commercial scope.
        if (!string.IsNullOrWhiteSpace(endpoint) &&
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            uri.Host.EndsWith(".azure.us", StringComparison.OrdinalIgnoreCase))
        {
            return UsGovernment;
        }

        return Commercial;
    }
}

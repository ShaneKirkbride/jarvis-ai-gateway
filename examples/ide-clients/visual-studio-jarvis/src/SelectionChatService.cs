using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.VisualStudio.Example;

public sealed class SelectionChatService
{
    private readonly GatewayClient _gatewayClient;
    private readonly ISecretStore _secretStore;

    public SelectionChatService(GatewayClient gatewayClient, ISecretStore secretStore)
    {
        _gatewayClient = gatewayClient ?? throw new ArgumentNullException(nameof(gatewayClient));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public static SelectionChatService CreateDefault() =>
        new(new GatewayClient(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }), new ProtectedDataSecretStore());

    public async Task<string> AskAboutSelectionAsync(string baseUrl, string model, string selectedText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            throw new GatewayClientException("Select text before asking Jarvis.");
        }

        string? storedApiKey = await _secretStore.GetDeveloperApiKeyAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(storedApiKey))
        {
            throw new GatewayClientException("Configure a Jarvis developer API key before asking Jarvis.");
        }

        Uri normalizedBaseUrl = GatewayClient.NormalizeBaseUrl(baseUrl);
        var messages = new List<ChatMessage>
        {
            new("system", "You are a careful coding assistant. Answer concisely and call out uncertainty."),
            new("user", "Review this selected code or text:\n\n" + selectedText)
        };

        string apiKey = storedApiKey!;
        return await _gatewayClient.CompleteChatAsync(normalizedBaseUrl, apiKey, model, messages, cancellationToken).ConfigureAwait(false);
    }
}

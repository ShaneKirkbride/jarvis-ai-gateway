using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jarvis.VisualStudio.Example;

public sealed class GatewayModel
{
    public GatewayModel(string id, string? displayName, string? provider, bool? supportsStreaming)
    {
        Id = id;
        DisplayName = displayName;
        Provider = provider;
        SupportsStreaming = supportsStreaming;
    }

    public string Id { get; }
    public string? DisplayName { get; }
    public string? Provider { get; }
    public bool? SupportsStreaming { get; }
}

public sealed class ChatMessage
{
    public ChatMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }

    public string Role { get; }
    public string Content { get; }
}

public sealed class GatewayClientException : Exception
{
    public GatewayClientException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException) => StatusCode = statusCode;

    public int? StatusCode { get; }
}

public sealed class GatewayClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public GatewayClient(HttpClient httpClient) => _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<IReadOnlyList<GatewayModel>> ListModelsAsync(Uri baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, baseUrl, "/models", apiKey);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GatewayModel>();
        }

        return data.EnumerateArray()
            .Where(item => item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
            .Select(item => new GatewayModel(
                item.GetProperty("id").GetString()!,
                TryGetString(item, "display_name"),
                TryGetString(item, "provider"),
                TryGetBoolean(item, "supports_streaming")))
            .ToArray();
    }

    public async Task<string> CompleteChatAsync(Uri baseUrl, string apiKey, string model, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new GatewayClientException("A Jarvis model must be configured.");
        if (messages.Count == 0) throw new GatewayClientException("At least one message is required.");

        var payload = new { model, stream = false, messages };
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, baseUrl, "/chat/completions", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);

        if (document.RootElement.TryGetProperty("choices", out JsonElement choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out JsonElement message) &&
            message.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    public static Uri NormalizeBaseUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("<gateway>", StringComparison.OrdinalIgnoreCase))
        {
            throw new GatewayClientException("Configure the Jarvis gateway base URL.");
        }

        string trimmed = value.Trim().TrimEnd('/');
        if (!trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) trimmed += "/v1";
        return new Uri(trimmed, UriKind.Absolute);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri baseUrl, string path, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !apiKey.StartsWith("jrvs_", StringComparison.Ordinal))
        {
            throw new GatewayClientException("Configure a valid Jarvis developer API key.");
        }

        var request = new HttpRequestMessage(method, new Uri(baseUrl, baseUrl.AbsolutePath.TrimEnd('/') + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string message = await ReadGatewayErrorAsync(response, cancellationToken).ConfigureAwait(false);
                throw new GatewayClientException(message, (int)response.StatusCode);
            }

            return response;
        }
        catch (GatewayClientException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new GatewayClientException("Unable to reach the Jarvis gateway.", innerException: ex);
        }
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new GatewayClientException("Jarvis returned an invalid JSON response.", (int)response.StatusCode, ex);
        }
    }

    private static async Task<string> ReadGatewayErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.TryGetProperty("message", out JsonElement message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "Jarvis request failed.";
            }
        }
        catch { }

        return $"Jarvis request failed with HTTP {(int)response.StatusCode}.";
    }

    private static string? TryGetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? TryGetBoolean(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
